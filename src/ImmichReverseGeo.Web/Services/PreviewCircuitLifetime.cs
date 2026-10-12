using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ImmichReverseGeo.Web.Services;

/// <summary>Stops page-owned work when its browser connection is lost.</summary>
public sealed class PreviewCircuitLifetime : CircuitHandler
{
    public event Action? Disconnected;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Disconnected?.Invoke();
        return Task.CompletedTask;
    }
}
