using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using VMCPlugin.BoneManipulation;
using VMCPlugin.Config;
using VMCPlugin.Offsets;
using VMCPlugin.Windows;
using VMCPlugin.Windows.Config;

namespace VMCPlugin;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/vmchead";

    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly Configuration config;
    private ConfigUi configUi;
    public CamController Cam { get; init; }
    private readonly VmcHeadReceiver vmcHead;
    private readonly RenderHook renderHook;
    private bool showConfig;

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commands,
        IFramework framework,
        IObjectTable objects,
        ICondition condition,
        IPartyList party,
        IPluginLog log,
        ISigScanner sigScanner,
        IGameInteropProvider interop,
        IGameGui gameGui,
        IDataManager dataManager)
    {
        Api.Init(pi);
        this.pi = pi;
        this.commands = commands;
        this.framework = framework;

        config = pi.GetPluginConfig() as Configuration ?? new Configuration();

        if (!config.SelfInitialized)
        {
            config.SelfInitialized = true;
            pi.SavePluginConfig(config);
        }
        pi.SavePluginConfig(config);

        Cam = new CamController(config);
        vmcHead = new VmcHeadReceiver(objects, log, config, Cam);
        renderHook = new RenderHook(sigScanner, interop, vmcHead, log);
        configUi = new ConfigUi(config, showConfig, pi, vmcHead);

        framework.Update += OnUpdate;

        pi.UiBuilder.Draw += OnDraw;
        pi.UiBuilder.OpenConfigUi += OpenConfig;

        commands.AddHandler(CommandName, new CommandInfo((_, args) =>
        {
            var a = args.Trim().ToLowerInvariant();
            if (a == "on") vmcHead.Enabled = true;
            else if (a == "off") vmcHead.Enabled = false;
            else vmcHead.Enabled = !vmcHead.Enabled;
            log.Information($"VMC Head Receiver: {(vmcHead.Enabled ? "AN" : "AUS")} (Port {vmcHead.Port})");
        })
        {
            HelpMessage = "VMC-Kopftracking ein-/ausschalten. Optionen: on, off"
        });

        if (config.Enabled)
            vmcHead.Enabled = config.Enabled;
    }

    private void OnUpdate(IFramework _)
    {
        vmcHead.UpdateEmotes();
        while (vmcHead.Commands.TryDequeue(out var cmd)) SendChat(cmd);
    }

    private static unsafe void SendChat(string cmd)
    {
        var s = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(cmd);
        FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance()->ProcessChatBoxEntry(s);
        s->Dtor(true);
    }

    private void OpenConfig() => showConfig = true;

    private void OnDraw()
    {
        if (!showConfig) return;
        configUi.OnDraw();
    }

    public void Dispose()
    {
        Cam.Dispose();
        vmcHead.Dispose();
        renderHook.Dispose();
        commands.RemoveHandler(CommandName);

        pi.UiBuilder.Draw -= OnDraw;
        pi.UiBuilder.OpenConfigUi -= OpenConfig;
        framework.Update -= OnUpdate;
        configUi.Dispose();
        pi.SavePluginConfig(config);
    }
}