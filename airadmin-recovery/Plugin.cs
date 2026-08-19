using System;
using System.Collections.Generic;
using AirAdmin.Recovery.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace AirAdmin.Recovery;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "AirAdmin Recovery";

    public override string Description =>
        "Emergency AirAdmin recovery helper. Credentials are never stored in plugin configuration or GitHub.";

    public override Guid Id => Guid.Parse("818b83c3-d9d8-4fd6-8771-ad3bf6876156");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = "AirAdmin.Recovery.Configuration.configPage.html"
        };
    }
}
