using System;
using MediaBrowser.Common.Plugins;

namespace AirAdmin.Recovery;

public sealed class Plugin : BasePlugin
{
    public override string Name => "AirAdmin Recovery";

    public override string Description => "One-shot recovery helper for starting the AirAdmin systemd service.";

    public override Guid Id => Guid.Parse("818b83c3-d9d8-4fd6-8771-ad3bf6876156");
}
