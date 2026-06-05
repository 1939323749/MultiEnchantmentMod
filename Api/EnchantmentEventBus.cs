using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MultiEnchantmentMod.Api;

internal static class EnchantmentEventBus
{
    private static readonly Dictionary<Type, List<Delegate>> Handlers = new();

    internal static IDisposable Subscribe<TEvent>(Action<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        GetOrCreateList(typeof(TEvent)).Add(handler);
        return new Subscription(typeof(TEvent), handler);
    }

    internal static IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        GetOrCreateList(typeof(TEvent)).Add(handler);
        return new Subscription(typeof(TEvent), handler);
    }

    internal static void Publish<TEvent>(TEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!Handlers.TryGetValue(typeof(TEvent), out List<Delegate>? list))
        {
            return;
        }

        foreach (Delegate handler in list.ToList())
        {
            try
            {
                if (handler is Action<TEvent> sync)
                {
                    sync(evt);
                }
            }
            catch (Exception ex)
            {
                MultiEnchantmentMod.Logger.Error(
                    $"[MultiEnchantment] Event handler for {typeof(TEvent).Name} threw: {ex}");
            }
        }
    }

    internal static async Task PublishAsync<TEvent>(TEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!Handlers.TryGetValue(typeof(TEvent), out List<Delegate>? list))
        {
            return;
        }

        foreach (Delegate handler in list.ToList())
        {
            try
            {
                if (handler is Action<TEvent> sync)
                {
                    sync(evt);
                }
                else if (handler is Func<TEvent, Task> async_)
                {
                    await async_(evt);
                }
            }
            catch (Exception ex)
            {
                MultiEnchantmentMod.Logger.Error(
                    $"[MultiEnchantment] Event handler for {typeof(TEvent).Name} threw: {ex}");
            }
        }
    }

    private static List<Delegate> GetOrCreateList(Type eventType)
    {
        if (!Handlers.TryGetValue(eventType, out List<Delegate>? list))
        {
            list = new List<Delegate>();
            Handlers[eventType] = list;
        }
        return list;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Type _eventType;
        private Delegate? _handler;

        public Subscription(Type eventType, Delegate handler)
        {
            _eventType = eventType;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_handler is not { } handler)
            {
                return;
            }

            _handler = null;
            if (Handlers.TryGetValue(_eventType, out List<Delegate>? list))
            {
                while (list.Remove(handler)) { }
            }
        }
    }
}
