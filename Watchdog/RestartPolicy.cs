using System;
using System.Collections.Generic;

namespace Watchdog;

public sealed class RestartPolicy
{
    private readonly Dictionary<string, RestartLimiter> _limiters = [];

    public bool CanRestart(string name, DateTimeOffset now, TimeSpan window, int maxRestarts)
    {
        if (!_limiters.TryGetValue(name, out var limiter))
        {
            limiter = new RestartLimiter();
            _limiters[name] = limiter;
        }

        return limiter.TryRegisterRestart(now, window, maxRestarts);
    }
}
