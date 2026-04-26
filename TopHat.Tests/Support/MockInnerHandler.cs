using System.Net;

namespace TopHat.Tests.Support;

/// <summary>
/// Test inner handler that captures every received request and returns a scripted response
/// (or throws a scripted exception). Used as the inner handler behind TopHatHandler in unit tests.
/// </summary>
internal sealed class MockInnerHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _behavior;

    public List<HttpRequestMessage> ReceivedRequests { get; } = new();

    public MockInnerHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> behavior)
    {
        this._behavior = behavior;
    }

    public static MockInnerHandler WithResponse(Func<HttpResponseMessage> responseFactory) =>
        new((_, _) => Task.FromResult(responseFactory()));

    public static MockInnerHandler WithStatus(HttpStatusCode status) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    public static MockInnerHandler WithException(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Capture a snapshot of the request for later assertions.
        this.ReceivedRequests.Add(request);
        return await this._behavior(request, cancellationToken).ConfigureAwait(false);
    }
}
