using VMCPlugin.BoneManipulation;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System;

namespace VMCPlugin.Windows;

// Signatur und Delegate stammen aus Customize+ (Aether-Tools/CustomizePlus, Apache-2.0):
// Core/Data/Constants.cs und Core/Services/HookingService.cs
public sealed class RenderHook : IDisposable
{
    private const string RenderSignature = "E8 ?? ?? ?? ?? 48 81 C3 ?? ?? ?? ?? BF ?? ?? ?? ?? 33 ED";

    private delegate nint RenderDelegate(nint a1, nint a2, nint a3, int a4);

    private readonly Hook<RenderDelegate> hook;
    private readonly VmcHeadReceiver vmc;
    private readonly IPluginLog log;
    private bool failed;

    public RenderHook(ISigScanner sigScanner, IGameInteropProvider interop, VmcHeadReceiver vmcRecv, IPluginLog log)
    {
        this.vmc = vmcRecv;
        this.log = log;

        var address = sigScanner.ScanText(RenderSignature);
        hook = interop.HookFromAddress<RenderDelegate>(address, Detour);
        hook.Enable();
    }

    private nint Detour(nint a1, nint a2, nint a3, int a4)
    {
        if (!failed)
        {
            try { vmc.Apply(); }
            catch (Exception ex)
            {
                failed = true;
                log.Error(ex, "Deformer im Render-Hook fehlgeschlagen, wird deaktiviert.");
            }
        }
        return hook.Original(a1, a2, a3, a4);
    }

    public void Dispose()
    {
        hook.Disable();
        hook.Dispose();
    }
}