namespace TopHat.Transforms;

/// <summary>
/// Escape-hatch transform that receives the full <see cref="HttpRequestMessage"/>. Use when you need
/// to mutate headers, replace content, or otherwise reach outside the parsed-body model served by
/// <see cref="IRequestTransform"/>.
/// </summary>
/// <remarks>
/// Raw transforms own the content. The pipeline does NOT re-serialize JSON after raw transforms run;
/// if you want the <c>MarkMutated</c> / re-serialize behavior, use <see cref="IRequestTransform"/> instead.
/// </remarks>
public interface IRawRequestTransform
{
    ValueTask InvokeAsync(RawRequestTransformContext context, CancellationToken cancellationToken);
}
