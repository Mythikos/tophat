using Microsoft.Extensions.DependencyInjection;
using TopHat.Configuration;
using TopHat.DependencyInjection;
using TopHat.Handlers;

namespace TopHat.Tests.Support;

/// <summary>
/// Builds a <see cref="TopHatHandler"/> wired through DI so the transform pipeline is active.
/// Returns the inner mock handler so tests can assert on what the upstream actually saw, plus
/// the service provider for arranging custom transform registrations before construction.
/// </summary>
internal static class TransformHandlerFactory
{
    public static (HttpClient Client, MockInnerHandler Inner, IServiceProvider Services) Build(
        Action<IServiceCollection> registerTransforms,
        Action<TopHatOptions>? configureOptions = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? behavior = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTopHat(configureOptions);
        registerTransforms(services);

        // Wire a named HttpClient with the TopHat handler chained to a captured MockInnerHandler.
        var inner = new MockInnerHandler(behavior ?? ((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK))));
        services.AddSingleton(inner);
        services.AddHttpClient("test")
            .AddHttpMessageHandler<TopHatHandler>()
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<MockInnerHandler>());

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test");
        return (client, inner, provider);
    }
}
