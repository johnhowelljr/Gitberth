using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Gitberth.Components;

public static class RenderModes
{
    /// <summary>Interactive server rendering without prerendering, so pages don't run their docker/git calls twice.</summary>
    public static readonly IComponentRenderMode Server = new InteractiveServerRenderMode(prerender: false);
}
