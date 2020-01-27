﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Documents;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    [TestClass]
    public class BatchAsyncStreamerTests
    {
        private const int DispatchTimerInSeconds = 5;
        private const int MaxBatchByteSize = 100000;
        private const int defaultMaxDegreeOfConcurrency = 10;
        private static Exception expectedException = new Exception();
        private ItemBatchOperation ItemBatchOperation = new ItemBatchOperation(OperationType.Create, 0, "0");
        private TimerPool TimerPool = new TimerPool(1);

        // Executor just returns a reponse matching the Id with Etag
        private BatchAsyncBatcherExecuteDelegate Executor
            = async (PartitionKeyRangeServerBatchRequest request, CancellationToken cancellationToken) =>
            {
                List<TransactionalBatchOperationResult> results = new List<TransactionalBatchOperationResult>();
                ItemBatchOperation[] arrayOperations = new ItemBatchOperation[request.Operations.Count];
                int index = 0;
                foreach (ItemBatchOperation operation in request.Operations)
                {
                    results.Add(
                    new TransactionalBatchOperationResult(HttpStatusCode.OK)
                    {
                        ResourceStream = new MemoryStream(new byte[] { 0x41, 0x42 }, index: 0, count: 2, writable: false, publiclyVisible: true),
                        ETag = operation.Id
                    });

                    arrayOperations[index++] = operation;
                }

                MemoryStream responseContent = await new BatchResponsePayloadWriter(results).GeneratePayloadAsync();

                SinglePartitionKeyServerBatchRequest batchRequest = await SinglePartitionKeyServerBatchRequest.CreateAsync(
                    partitionKey: null,
                    operations: new ArraySegment<ItemBatchOperation>(arrayOperations),
                    serializerCore: MockCosmosUtil.Serializer,
                cancellationToken: cancellationToken);

                await Task.Delay(10);

                TransactionalBatchResponse batchresponse = await TransactionalBatchResponse.FromResponseMessageAsync(
                    new ResponseMessage(HttpStatusCode.OK) { Content = responseContent },
                    batchRequest,
                    MockCosmosUtil.Serializer);

                return new PartitionKeyRangeBatchExecutionResult(request.PartitionKeyRangeId, request.Operations, batchresponse);
            };

        private BatchAsyncBatcherExecuteDelegate ExecutorWithFailure = (PartitionKeyRangeServerBatchRequest request, CancellationToken cancellationToken) =>
        {
            throw expectedException;
        };

        private BatchAsyncBatcherRetryDelegate Retrier = (ItemBatchOperation operation, CancellationToken cancellation) =>
        {
            return Task.CompletedTask;
        };

        [DataTestMethod]
        [ExpectedException(typeof(ArgumentOutOfRangeException))]
        [DataRow(0)]
        [DataRow(-1)]
        public void ValidatesSize(int size)
        {
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(size, MaxBatchByteSize, DispatchTimerInSeconds, this.TimerPool, null, false, 0, MockCosmosUtil.Serializer, this.Executor, this.Retrier);
        }

        [DataTestMethod]
        [ExpectedException(typeof(ArgumentOutOfRangeException))]
        [DataRow(0)]
        [DataRow(-1)]
        public void ValidatesDispatchTimer(int dispatchTimerInSeconds)
        {
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(1, MaxBatchByteSize, dispatchTimerInSeconds, this.TimerPool, null, false, 0, MockCosmosUtil.Serializer, this.Executor, this.Retrier);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void ValidatesExecutor()
        {
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(1, MaxBatchByteSize, DispatchTimerInSeconds, this.TimerPool, null, false, 0, MockCosmosUtil.Serializer, null, this.Retrier);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void ValidatesRetrier()
        {
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(1, MaxBatchByteSize, DispatchTimerInSeconds, this.TimerPool, null, false, 0, MockCosmosUtil.Serializer, this.Executor, null);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void ValidatesSerializer()
        {
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(1, MaxBatchByteSize, DispatchTimerInSeconds, this.TimerPool, null, false, 0, null, this.Executor, this.Retrier);
        }

        [TestMethod]
        public async Task ExceptionsOnBatchBubbleUpAsync()
        {
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(2, MaxBatchByteSize, DispatchTimerInSeconds, this.TimerPool, null, false, 0, MockCosmosUtil.Serializer, this.ExecutorWithFailure, this.Retrier);
            ItemBatchOperationContext context = AttachContext(this.ItemBatchOperation);
            batchAsyncStreamer.Add(this.ItemBatchOperation);
            Exception capturedException = await Assert.ThrowsExceptionAsync<Exception>(() => context.OperationTask);
            Assert.AreEqual(expectedException, capturedException);
        }

        [TestMethod]
        public async Task TimerDispatchesAsync()
        {
            // Bigger batch size than the amount of operations, timer should dispatch
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(2, MaxBatchByteSize, DispatchTimerInSeconds, this.TimerPool, null, false, 0, MockCosmosUtil.Serializer, this.Executor, this.Retrier);
            ItemBatchOperationContext context = AttachContext(this.ItemBatchOperation);
            batchAsyncStreamer.Add(this.ItemBatchOperation);
            TransactionalBatchOperationResult result = await context.OperationTask;

            Assert.AreEqual(this.ItemBatchOperation.Id, result.ETag);
        }

        [TestMethod]
        public async Task ValidatesCongestionControlAsync()
        {
            SemaphoreSlim newLimiter = new SemaphoreSlim(1, defaultMaxDegreeOfConcurrency);
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(2, MaxBatchByteSize, 1, this.TimerPool, newLimiter, true, defaultMaxDegreeOfConcurrency, MockCosmosUtil.Serializer, this.Executor, this.Retrier);

            Assert.AreEqual(newLimiter.CurrentCount, 1);

            List<Task<TransactionalBatchOperationResult>> contexts = new List<Task<TransactionalBatchOperationResult>>(100);
            for (int i = 0; i < 200; i++)
            {
                ItemBatchOperation operation = new ItemBatchOperation(OperationType.Create, i, i.ToString());
                ItemBatchOperationContext context = AttachContext(operation);
                batchAsyncStreamer.Add(operation);
                contexts.Add(context.OperationTask);
            }

            // 100 batch request should sum up to 1000 ms barrier with wait time of 10ms in executor
            await Task.WhenAll(contexts);

            // Only one time change is expected as within 1 seconds all the request will get executed parallely.
            Assert.AreEqual(newLimiter.CurrentCount, 2);

            await Task.Delay(2000);

            // No change when adding extra delays. Without requests time summing up to 1000 ms, there should be no change in handle count.
            Assert.AreEqual(newLimiter.CurrentCount, 2);
        }

        [TestMethod]
        public async Task DispatchesAsync()
        {
            // Expect all operations to complete as their batches get dispached
            BatchAsyncStreamer batchAsyncStreamer = new BatchAsyncStreamer(
                2,
                MaxBatchByteSize,
                DispatchTimerInSeconds,
                this.TimerPool,
                null,
                false,
                0,
                MockCosmosUtil.Serializer,
                this.Executor,
                this.Retrier);
            List<Task<TransactionalBatchOperationResult>> contexts = new List<Task<TransactionalBatchOperationResult>>(10);
            for (int i = 0; i < 10; i++)
            {
                ItemBatchOperation operation = new ItemBatchOperation(OperationType.Create, i, i.ToString());
                ItemBatchOperationContext context = AttachContext(operation);
                batchAsyncStreamer.Add(operation);
                contexts.Add(context.OperationTask);
            }

            await Task.WhenAll(contexts);

            for (int i = 0; i < 10; i++)
            {
                Task<TransactionalBatchOperationResult> context = contexts[i];
                Assert.AreEqual(TaskStatus.RanToCompletion, context.Status);
                TransactionalBatchOperationResult result = await context;
                Assert.AreEqual(i.ToString(), result.ETag);
            }
        }

        private static ItemBatchOperationContext AttachContext(ItemBatchOperation operation)
        {
            ItemBatchOperationContext context = new ItemBatchOperationContext(string.Empty);
            operation.AttachContext(context);
            return context;
        }
    }
}
