// Copyright (C) KitWright. All rights reserved.

using System.Threading.Tasks;
using KitWright.Editor.Threading;
using NUnit.Framework;

namespace KitWright.Editor
{
    public sealed class EditorThreadHelperLifecycleTests
    {
        [Test]
        public void ExecuteAsyncOnEditorThreadAsync_CancelsQueuedOuterTaskWhenDisposed()
        {
            var helper = new EditorThreadHelper();
            Task<int> queuedTask = null;

            Task.Run(() =>
            {
                queuedTask = helper.ExecuteAsyncOnEditorThreadAsync(async () =>
                {
                    await Task.Yield();
                    return 42;
                });
            }).Wait();

            helper.Dispose();

            Assert.IsNotNull(queuedTask);
            Assert.IsTrue(queuedTask.IsCanceled);
        }

        [Test]
        public void ExecuteOnEditorThreadAsync_CancelsQueuedGenericTaskWhenDisposed()
        {
            var helper = new EditorThreadHelper();
            Task<int> queuedTask = null;

            Task.Run(() =>
            {
                queuedTask = helper.ExecuteOnEditorThreadAsync(() => 42);
            }).Wait();

            helper.Dispose();

            Assert.IsNotNull(queuedTask);
            Assert.IsTrue(queuedTask.IsCanceled);
        }

        [Test]
        public void ExecuteAsyncOnEditorThreadAsync_RejectsNewWorkAfterDispose()
        {
            var helper = new EditorThreadHelper();
            helper.Dispose();

            var rejectedTask = helper.ExecuteAsyncOnEditorThreadAsync(() => Task.FromResult(42));

            Assert.IsTrue(rejectedTask.IsCanceled);
        }
    }
}
