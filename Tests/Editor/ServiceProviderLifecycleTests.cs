// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using KitWright.Editor.DI;
using NUnit.Framework;

namespace KitWright.Editor
{
    public sealed class ServiceProviderLifecycleTests
    {
        [Test]
        public void Dispose_DisposesServicesInReverseCreationOrder()
        {
            var disposeEvents = new List<string>();
            var services = new ServiceCollection();
            services.AddSingleton<FirstDisposable>(_ => new FirstDisposable(disposeEvents));
            services.AddSingleton<SecondDisposable>(_ => new SecondDisposable(disposeEvents));

            using (var provider = services.BuildServiceProvider())
            {
                provider.GetService(typeof(FirstDisposable));
                provider.GetService(typeof(SecondDisposable));
            }

            CollectionAssert.AreEqual(new[] { "second", "first" }, disposeEvents);
        }

        private sealed class FirstDisposable : IDisposable
        {
            private readonly List<string> _events;

            public FirstDisposable(List<string> events)
            {
                _events = events;
            }

            public void Dispose()
            {
                _events.Add("first");
            }
        }

        private sealed class SecondDisposable : IDisposable
        {
            private readonly List<string> _events;

            public SecondDisposable(List<string> events)
            {
                _events = events;
            }

            public void Dispose()
            {
                _events.Add("second");
            }
        }
    }
}
