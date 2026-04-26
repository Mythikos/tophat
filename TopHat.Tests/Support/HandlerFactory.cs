using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TopHat.Configuration;
using TopHat.Handlers;

namespace TopHat.Tests.Support;

/// <summary>
/// Builds a <see cref="TopHatHandler"/> chained to a <see cref="MockInnerHandler"/> and exposes an
/// <see cref="HttpClient"/> ready for test calls.
/// </summary>
internal static class HandlerFactory
{
    public static (HttpClient Client, MockInnerHandler Inner, TopHatOptions Options) Build(
        Action<TopHatOptions>? configure = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? behavior = null)
    {
        var options = new TopHatOptions();
        configure?.Invoke(options);
        var optionsWrapper = Options.Create(options);
        var inner = new MockInnerHandler(behavior ?? ((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK))));
        var handler = new TopHatHandler(optionsWrapper, NullLogger<TopHatHandler>.Instance)
        {
            InnerHandler = inner,
        };
        var client = new HttpClient(handler);
        return (client, inner, options);
    }
}
