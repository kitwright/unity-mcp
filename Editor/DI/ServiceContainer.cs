// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;

namespace KitWright.Editor.DI
{
    internal class ServiceDescriptor
    {
        public Type ServiceType { get; set; }
        public Type ImplementationType { get; set; }
        public Func<IServiceProvider, object> Factory { get; set; }
    }

    internal class ServiceCollection
    {
        private readonly List<ServiceDescriptor> _descriptors = new List<ServiceDescriptor>();

        public ServiceCollection AddSingleton<TService, TImplementation>() where TImplementation : TService
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                ImplementationType = typeof(TImplementation)
            });
            return this;
        }

        public ServiceCollection AddSingleton<TService>(Func<IServiceProvider, TService> factory)
        {
            _descriptors.Add(new ServiceDescriptor
            {
                ServiceType = typeof(TService),
                Factory = sp => factory(sp)
            });
            return this;
        }

        public ServiceProvider BuildServiceProvider()
        {
            return new ServiceProvider(_descriptors.ToList());
        }
    }

    internal class ServiceProvider : IServiceProvider, IDisposable
    {
        private readonly List<ServiceDescriptor> _descriptors;
        private readonly Dictionary<Type, object> _singletonInstances = new Dictionary<Type, object>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly object _lock = new object();

        public ServiceProvider(List<ServiceDescriptor> descriptors)
        {
            _descriptors = descriptors;
        }

        public object GetService(Type serviceType)
        {
            var descriptor = _descriptors.LastOrDefault(d => d.ServiceType == serviceType);
            if (descriptor == null) return null;

            return GetOrCreateSingleton(descriptor);
        }

        private object GetOrCreateSingleton(ServiceDescriptor descriptor)
        {
            lock (_lock)
            {
                if (_singletonInstances.TryGetValue(descriptor.ServiceType, out var existing))
                    return existing;

                var instance = CreateInstance(descriptor);
                if (instance != null)
                {
                    _singletonInstances[descriptor.ServiceType] = instance;
                    if (instance is IDisposable disposable)
                        _disposables.Add(disposable);
                }
                return instance;
            }
        }

        private object CreateInstance(ServiceDescriptor descriptor)
        {
            if (descriptor.Factory != null)
                return descriptor.Factory(this);

            if (descriptor.ImplementationType != null)
                return ActivateType(descriptor.ImplementationType);

            return null;
        }

        private object ActivateType(Type type)
        {
            var constructors = type.GetConstructors();
            if (constructors.Length == 0)
                return Activator.CreateInstance(type);

            var ctor = constructors.OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = ctor.GetParameters();
            var args = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = GetService(parameters[i].ParameterType);
                if (args[i] == null && !parameters[i].HasDefaultValue)
                    throw new InvalidOperationException(
                        $"Cannot resolve parameter '{parameters[i].Name}' of type '{parameters[i].ParameterType.Name}' for '{type.Name}'.");
                if (args[i] == null)
                    args[i] = parameters[i].DefaultValue;
            }

            return ctor.Invoke(args);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                for (var i = _disposables.Count - 1; i >= 0; i--)
                {
                    var disposable = _disposables[i];
                    try { disposable.Dispose(); } catch { }
                }
                _disposables.Clear();
                _singletonInstances.Clear();
            }
        }
    }
}
