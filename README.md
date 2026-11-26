# VMCPlugin

A **Dalamud** plugin for *Final Fantasy XIV* that receives **Virtual Motion Capture (VMC)** protocol data over UDP/OSC and applies real-time full-body bone retargeting, 
facial expression tracking, hair physics/adjustments, first-person head camera tracking, and blend-shape-triggered emotes directly to the local player character.

## Warning:

That's just a plogon I made for fun. 

This plogon was written by Claude (free) and me (for the stuff Claude wasn't capable of).

The README was written by gemini (free).

---

## Features

* **Real-Time VMC / OSC Receiver**
* **Full-Body & Upper-Body Bone Retargeting**
* **Facial Tracking & Hair Manipulation**
* **First-Person Head Camera Integration**
* **Blend-Shape-Driven Emotes**

---

## Project Structure

```text
VMCPlugin/
├── Plugin.cs                         # Main Dalamud plugin entry point & lifecycle management
├── BoneManipulation/
│   ├── VmcReceiver.cs                # UDP/OSC listener, packet parser, and bone/face retargeting engine
│   ├── RenderHook.cs                 # Render pipeline hook applying skeleton modifications per frame
│   ├── CamaraManager.cs              # First-person camera controller & head-tracking synchronization
│   └── VmcHair.cs                    # Hair bone manipulation and adjustments
├── Config/
│   └── Configuration.cs              # Plugin settings, bone presets, face rules, and emote rules
└── Windows/
    ├── MainWindow.cs                 # Main plugin status & quick-control UI window
    └── Config/
        └── ConfigUi.cs               # Detailed configuration UI (ports, presets, face/emote rules)
```

---

## Requirements

* for body tracking: a **VMC-compatible tracking application** capable of streaming OSC data over UDP (e.g., *VSeeFace*, *Warudo*, *VNyan*, *iFacialMocap* via VMC bridge, *Virtual Motion Capture*, etc.)
* as a receiver: internet and a connection to a server

---

## Server

### Build via Docker

* docker build -t xivbone . 
* docker run -d --restart unless-stopped -p 40000:40000 xivbone


## Disclaimer

This plugin modifies character skeleton transforms client-side in memory.