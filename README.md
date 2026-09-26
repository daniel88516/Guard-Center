# Guard Center

## Quick start: Run the exe directly or build it with build.bat

**Guard Center can be packaged as a single `Guard Center.exe` that runs directly on Windows x64. You can also download the source code and double-click `build.bat` to create the exe yourself, without installing or opening Visual Studio.**

### If you already have the built exe

Double-click the single-file `Guard Center.exe` to use it. You do not need to install the .NET Desktop Runtime or SDK separately.
On first launch, it automatically extracts the bundled components it needs to `%LOCALAPPDATA%\Guard Center\Portable`, then starts the main program.

### Build the exe from source

1. Download this repository as a ZIP and extract it completely, or clone it with Git.
2. If the SDK is not installed, install the **Windows x64 version of the .NET 8 SDK** from the [official Microsoft download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).
3. Double-click [`build.bat`](build.bat) in the project root and wait for the build to finish. You can also run it in PowerShell:

   ```powershell
   .\build.bat
   ```

4. Open `dist` and run **`Guard Center.exe`** inside it. `dist` contains only this file, which you can copy by itself to other Windows x64 users. The build also updates the exe with the same name in the project root.

.NET automatically downloads the required NuGet packages and publishing runtime components. Keep an internet connection available for the first build.
The `restore` message is a normal step that retrieves project dependencies.
Build messages are in English. When the build ends, the window stays open until you press any key.

### If you are prompted about missing components

| Message or situation | What to do |
| --- | --- |
| `dotnet` cannot be found, or the .NET SDK is reported as missing | Install the .NET 8 SDK from the official link above, then reopen `build.bat`. |
| Package downloads fail or NuGet cannot be reached | Check your internet connection and NuGet sources, then run `build.bat` again. Dependencies will be downloaded automatically. |
| UAC Guard or another feature prompts for additional components | Follow that module's setup or installation prompt to add the required components, and approve Windows UAC when needed. Hardware features also require compatible devices and drivers. |

The single-file instructions above apply to `dist\Guard Center.exe` produced by `build.bat`.
The output of a regular `dotnet build` still requires its accompanying files and the .NET Desktop Runtime.
The GitHub Download ZIP currently contains source code; there are no public release binaries yet. You can build one using the steps above.

---

## Project overview

Guard Center is a Windows desktop system utility that brings together features otherwise scattered across Windows, drivers, Control Panel, and different applications into one interface.

It currently includes:

- Audio Guard
- Device Guard
- Keyboard Guard
- Game Helper
- App Guard
- Link Guard
- Display Guard
- Power Guard
- UAC Guard
- VSR Guard

Guard Center is built with **C# / .NET 8 / WPF**.

---

# Main features

## Audio Guard

Audio Guard manages Windows audio sessions and performs protective actions when audio devices change.

### App Mixer

App Mixer lists the Core Audio sessions currently available.

For each application, you can:

- Adjust its volume
- Mute or unmute it
- See which programs are currently using audio

If a program does not appear in the list, play audio from it once and then return to Audio Guard.

Guard Center updates audio sessions only when needed, avoiding unnecessary heavy operations while you simply browse the interface.

---

## Audio Zero Guard

Audio Zero Guard protects you when the audio output device changes.

For example:

1. You are using headphones.
2. The headphones are suddenly unplugged.
3. Windows automatically switches to speakers.
4. The new output device might retain the previous high volume.
5. Audio Zero Guard detects the change in audio topology and immediately applies the configured protection.

Available options:

### Set mute

Mute the current playback device after an audio device switch.

### Set volume to 0

Set the playback device's master volume to `0` after an audio device switch.

### Zero audio when enabled

Apply the currently configured protection once as soon as Audio Guard is enabled.

### React to property changes

In addition to devices being added, removed, or switched, treat changes to driver or audio endpoint properties as events that require the protection to be reapplied.

### Poll interval

Set how often Audio Guard checks the audio topology.

The unit is milliseconds.

### Retry delays

Set additional check times after a device switch.

Some Windows audio drivers do not finish updating all their state immediately when a device switches, so Guard Center can apply the protection again later.

---

# Device Guard

Device Guard checks the state of Windows hardware and Plug and Play (PnP) devices.

It has three parts:

1. Currently detectable devices
2. Core computer hardware
3. Input device stack

---

## Currently detectable devices

Lists PnP devices that Windows can still recognize, such as:

- Bluetooth adapters
- USB cameras
- USB audio devices
- Other PnP devices that can be restarted

Guard Center can restart a selected device to address cases where:

- A device is unresponsive
- A driver has a temporary problem
- A USB device is not working properly
- A Bluetooth, camera, or audio device needs to be reinitialized

---

## Core computer hardware

Guard Center determines the computer's main hardware capabilities from the complete Windows device tree, including:

- Bluetooth
- Graphics
- Network
- Audio
- Camera
- USB

It distinguishes states such as:

- Healthy
- Faulty
- Disabled
- Missing
- Driver required
- Restart required
- Unsupported
- Ambiguous identity

Repair actions have risk levels:

- Low
- Medium
- High
- Critical

Higher-risk repairs are not performed without explicit authorization.

Some device repairs may require administrator privileges, a Windows restart, or a driver reinstall.

---

## Input device stack

This area primarily checks specialized keyboard and mouse input stacks.

Current checks include:

- Interception
- HuaJuan compatibility layer
- Windows input devices
- Device namespace

Guard Center tries to identify input devices by hardware identity instead of relying on Windows device numbers that may change when devices are reconnected.

---

# Keyboard Guard

Keyboard Guard brings together several Windows keyboard features that are scattered across settings but often affect everyday use.

## Disable Shift + Space width toggle

In Chinese input methods such as Microsoft Bopomofo, `Shift + Space` may toggle between:

- Full-width characters
- Half-width characters

Enabling this feature prevents that full-width/half-width toggle.

Shift and Space still reach the active application normally; the keys themselves are not blocked.

## Disable the five-Shift Sticky Keys shortcut

Disables the Windows shortcut:

**Press Shift five times → Sticky Keys**

This does not disable Shift itself or affect:

- Shift + a letter
- Ctrl + Shift
- Shift controls in games

Guard Center changes only the Sticky Keys shortcut behavior.

## Windows keyboard settings

Open these Windows pages directly:

- Typing Settings
- Language Settings

You do not need to find them in Windows Settings yourself.

---

# Game Helper

Game Helper provides system features related to gaming controls.

## Screen Crosshair

Display an FPS crosshair in the center of the screen.

The crosshair:

- Stays on top
- Has a transparent background
- Lets mouse input pass through
- Does not intercept mouse input in games

You can control it from the Guard Center main window or toggle it directly from the system tray:

`Screen crosshair: On / Off`

## Only show in selected apps

You can limit the crosshair to appear **only while selected programs are in the foreground**.

For example, if you add only:

`game.exe`

the crosshair will not appear on the desktop, in a browser, or in other programs.

## Keep Enhance pointer precision off

The Windows setting **Enhance pointer precision** is Windows mouse acceleration.

When this guard is enabled:

1. Guard Center immediately turns off Enhance pointer precision.
2. It checks again every 30 seconds.
3. If another program turns mouse acceleration back on, Guard Center turns it off again.

This is a system-wide setting; it is not tied to a single game.

---

# Game Protection Settings

Game Helper can create a protection profile for each game.

Each app can be configured independently.

Current options include:

### Windows Key Protection

Block the Windows key while a game runs to prevent accidental switches to the desktop.

### Right Ctrl + D

Provide special Show Desktop behavior while gaming.

### Input Method Protection

Control Microsoft ENG / Windows input method behavior for games to prevent unexpected input method changes during play.

## Manage apps

Game Helper's app management has two areas:

### Protected apps

Apps already added to Game Protection.

### All apps

Guard Center builds an application catalog from Windows installed programs, the Start Menu, and other sources.

Background loading starts only when you switch to All apps for the first time. Simply opening Game Helper does not scan the entire computer.

After adding an app, you can configure its Game Protection options.

---

# App Guard

App Guard is Guard Center's application management hub.

It combines sources into an application catalog, including:

- Windows Registry
- Start Menu
- Installed apps
- Steam
- Executable information

Guard Center tries to merge records from different sources that refer to the same application instead of showing many duplicates.

## App information

Expand an app to see information such as:

- App name
- Publisher
- Executable path
- Install location
- Source
- Running state
- Uninstall information

## App actions

Depending on the information available for an app, you can:

- Launch the app
- Open related locations
- Manage running processes
- Terminate
- Uninstall

### Terminate

Only processes matching that executable path are terminated.

You are asked to confirm again before the action runs.

Guard Center does not allow you to terminate Guard Center itself from App Guard.

### Uninstall

If the Windows application catalog contains a valid uninstall command, App Guard can start that uninstall process.

You are also asked to confirm before it runs.

---

# Explorer Integration

App Guard can add an entry to the Windows Explorer context menu.

Enable **Shortcut right-click entry** to add a Guard Center entry for:

- `.exe`
- `.lnk`

You can then select a program in Explorer and send it to App Guard.

Turning this option off removes the integration.

---

# Link Guard

Link Guard creates launch and running-state relationships between applications.

It is useful for two programs that should run together.

For example:

```text
Game.exe → Helper.exe
```

When the game starts, the helper starts automatically.

## One-way

```text
A → B
```

This means:

> While A runs, B must run too.

But:

> Running B alone does not start A.

For example:

```text
Game.exe → MonitoringTool.exe
```

## Bidirectional

```text
A ↔ B
```

This is a two-way link.

Starting either app automatically starts the other.

## Configure each rule independently

Each link rule can independently control:

- Enabled
- One-way / Bidirectional
- Whether to launch through gsudo
- What happens to the linked app when the other exits
- Whether to keep the linked app running
- Launch delay

Different pairs of applications can therefore have entirely different behavior.

## Manage linked apps

Select **Manage apps** to access two tabs:

### All apps

Lists apps in the application catalog.

Select the trigger app, then choose the app to link to it.

### Linked apps

Shows existing link rules so you can inspect and change their relationships.

---

# Display Guard

Display Guard controls supported physical monitors.

Depending on the monitor and driver, Guard Center may use:

- DDC/CI
- WMI
- Windows Display APIs

to access hardware controls.

Not every monitor supports every feature.

## Brightness

If a monitor supports hardware brightness, you can adjust it directly from Guard Center.

Available controls:

- Slider
- `+`
- `−`

The buttons adjust brightness in 1% increments.

## Contrast

If a monitor exposes contrast control, you can also adjust it from Guard Center.

The option is not forced onto monitors that do not expose contrast control.

# Multi-monitor Sync

If you have multiple supported monitors, you can use:

- Sync Brightness
- Sync Contrast

to adjust all monitors that support the selected feature at once.

Unsupported monitors are skipped.

# Display Modes

Display Guard includes:

- Standard
- Reading
- Scenery
- Movie
- Game
- Custom
- Live

Profiles are saved for each physical display.

In a multi-monitor setup, each monitor can therefore have its own brightness and contrast values.

If a monitor in a profile is currently disconnected, applying the profile skips it without preventing other monitors from being updated.

## Live Mode

Live Mode saves the actual adjustments you make.

It is useful when you want to use Guard Center directly as a monitor hardware control panel.

## Custom Mode

Save the current state of all supported monitors as your own Custom Profile.

## Notes on monitor control

A physical monitor is not an ordinary value in memory.

DDC/WMI commands can take tens to hundreds of milliseconds to finish. Guard Center therefore:

- Runs hardware operations in sequence
- Avoids generating large numbers of hardware commands while a slider is dragged
- Prevents an older hardware readback from overwriting a value the user just set

If a monitor has just:

- Turned on
- Resumed from sleep
- Been disconnected and reconnected
- Changed display configuration

you can use Refresh to detect it again.

---

# Power Guard

Power Guard temporarily prevents Windows from sleeping automatically due to inactivity.

It uses the Windows Power Request API.

**It does not directly change your existing Windows power plan.**

## Keep awake

After you turn on **Keep awake**, Windows will not automatically enter sleep because of the idle timer.

You can still manually:

- Sleep
- Shut down
- Restart

## Keep display on

You can also turn on **Keep display on**. Then:

- The system stays awake
- The display stays on

If you turn this option off:

- The system still stays awake
- The display turns off according to the normal Windows settings

# Duration

You can set how long Power Guard remains active.

Options include a fixed duration and:

### Until Manual

Power Guard stays active until you turn it off yourself.

### Custom

Set a custom duration from **1 minute to 30 days**.

## Countdown timeline

Fixed-duration modes show the remaining time.

You can drag the timeline to change the remaining time directly.

Changing the duration restarts the countdown from the current time.

## Power Guard limitations

Power Guard uses a standard Windows Power Request.

Its actual effect may still be influenced by:

- Windows policy
- Modern Standby
- Battery policy
- Lock screen
- OEM power management

---

# VSR Guard

VSR Guard configures **NVIDIA RTX Video Super Resolution**.

It currently focuses on Google Chrome.

## Requirements

Full VSR functionality requires:

- An NVIDIA RTX GPU
- A working NVIDIA driver
- Google Chrome
- Chrome graphics acceleration
- Windows assigning Chrome to the high-performance GPU
- NVIDIA RTX Video Super Resolution enabled

Guard Center checks each requirement.

## Readiness

VSR Guard shows:

### NVIDIA RTX GPU

Checks:

- Whether an NVIDIA GPU is present
- Whether it is a GPU that supports RTX VSR
- Whether the driver is working

### Google Chrome

Checks:

- Whether Chrome is installed
- Chrome version
- Chrome executable path

### Power source

When a notebook runs on battery power, the browser may favor lower-power processing.

AC power is therefore recommended when you need RTX video enhancement.

## Required Settings

Guard Center checks the following in order:

### 1. Chrome High performance GPU

Checks whether Windows Graphics Preference assigns Chrome to the high-performance GPU.

This setting is especially important on Optimus notebooks.

### 2. Chrome graphics acceleration

Checks whether **Use graphics acceleration when available** is enabled in Chrome.

Changing this setting usually requires restarting Chrome.

### 3. NVIDIA RTX Video Super Resolution

Checks whether the VSR flag is enabled in the NVIDIA display driver.

## Set up all

If the hardware requirements are met, select **Set up all** to have Guard Center complete the settings it can change automatically.

Some actions may require:

- Closing Chrome
- Administrator privileges
- Restarting Chrome

## NVIDIA Control Panel

VSR Guard can also open NVIDIA Control Panel directly.

There you can manage NVIDIA's native settings, including:

- Super Resolution
- Quality
- HDR
- Deinterlacing
- Inverse Telecine

---

# UAC Guard

UAC Guard has the highest level of privilege among Guard Center's features and should be understood before use.

It is mainly intended for users of the **OpenAI Codex Windows App**. It lets Codex run administrator commands through a controlled `gsudo` session when needed, instead of repeatedly showing Windows UAC prompts.

**The ChatGPT / Codex GUI itself continues to run with standard user privileges.**

UAC Guard does not disable Windows UAC.

# UAC Guard components

UAC Guard checks:

- OpenAI.Codex AppX package
- gsudo
- Guard Center UAC Host
- Windows scheduled task
- Protected Host in Program Files
- ACLs
- Current gsudo session

## One-click install/repair

When using UAC Guard for the first time, select **One-click install/repair**.

Guard Center creates the required protected components.

This step asks for Windows UAC authorization once.

# Authorization modes

There are currently two modes.

## Codex process lifecycle

**Recommended mode.**

Guard Center detects the PID of the `Codex app-server`.

Authorization is limited to:

- That Codex process
- Child processes it creates

When Codex exits, the authorized session is revoked.

The next time Codex starts, Guard Center binds authorization to its new PID.

This mode provides stronger isolation.

## Guard Center lifecycle

High-risk mode.

Guard Center creates a gsudo session while it is open.

The session is revoked only when **Guard Center fully exits**.

This means authorization does not need to be recreated if Codex restarts.

The trade-off is:

> During that time, other programs run by the same Windows user may also be able to use the current gsudo cache.

Use this mode only when your workflow requires it.

## Redetect/authorize now

Manually recreate the authorization session for the selected mode.

## Terminate session now

Immediately run:

```text
gsudo -k
```

This revokes the current administrator session without closing Guard Center.

## Inspect the actual setup

UAC Guard provides shortcuts to:

### Task Scheduler

Open Windows Task Scheduler directly.

### Protected directory

Inspect the UAC Guard Host installed in Program Files directly.

The authorization mechanism therefore does not depend on invisible background state.

## Uninstall UAC Guard

This removes only:

- The UAC Guard scheduled task
- The Protected Host
- UAC Guard automatic authorization components
- Files left by older versions of the Codex Administrator Launcher

It does not delete:

- Codex
- ChatGPT
- Conversations
- Other Guard Center settings

It also does not change Windows UAC policy.

---

# System tray

Guard Center creates a system tray icon when it starts.

The tray provides quick access to:

- Open Guard Center
- Power Guard
- Keep awake
- Keep display on
- Power Guard duration
- Screen Crosshair
- Zero playback devices now
- Open Device Guard
- Exit

Double-click the tray icon to reopen the main window.

---

# Launch at Windows startup

Under **Settings → Startup**, enable **Launch at Windows startup**.

After you sign in to Windows, Guard Center will:

1. Start automatically
2. Minimize to the system tray

It will not keep the main window on the desktop.

---

# Application Icon

You can change the Guard Center icon in Settings.

Supported formats:

- PNG
- JPG / JPEG
- BMP
- ICO

The custom icon is used for:

- Guard Center window
- Taskbar
- System tray
- Startup shortcut
- Explorer Integration
- App Guard

Select **Reset to default** to restore the built-in icon.

---

# Interface controls

Use the left sidebar to switch modules.

Drag modules to reorder them; the order is saved automatically.

Guard Center also supports UI scaling.

Use `Ctrl + Mouse Wheel` to adjust the interface scale.

The current range is:

```text
85% to 135%
```

---

# Saving settings

Guard Center automatically saves user settings, including:

- Audio Guard
- Game Helper
- App Protection
- Link Guard rules
- Display profiles
- Power Guard
- UAC Guard mode
- Sidebar order
- UI scale
- Custom icon

You do not need to configure them again after closing and restarting normally.

---

# System requirements

## Operating system

Guard Center is a Windows-only application.

Recommended:

- Windows 10
- Windows 11

Some features depend on modern Windows APIs, so Windows 11 is the primary target environment.

## Runtime / development environment

Project target framework:

```text
net8.0-windows
```

Technologies used:

- .NET 8
- WPF
- Windows Forms interoperability
- System.Management
- WPF-UI

---

# Run from source

The GitHub repository currently has no official Releases, so you can build directly from source.

First install the **.NET 8 SDK**.

Then clone:

```powershell
git clone https://github.com/daniel88516/Guard-Center.git
cd Guard-Center
```

Restore:

```powershell
dotnet restore "src/Guard Center.csproj"
```

Build:

```powershell
dotnet build "src/Guard Center.csproj"
```

Run:

```powershell
dotnet run --project "src/Guard Center.csproj"
```

Or run this directly:

```text
src\bin\Debug\net8.0-windows\Guard Center.exe
```

# Build a distributable version

On a Windows x64 computer with the .NET 8 SDK installed, run this from the project root:

```powershell
.\build.bat
```

The script publishes the main program and UAC Guard Host, then packages them as `dist\Guard Center.exe`.
**The final `dist` directory contains only this file**. You can distribute the exe by itself without the source code or `bin` directory.
Build messages are in English; the window stays open after success or failure until you press any key.

On first launch on Windows x64, the single-file launcher automatically extracts the bundled program files and .NET 8 runtime to `%LOCALAPPDATA%\Guard Center\Portable`, then starts the main program.
Users therefore do not need to install the .NET Desktop Runtime, open an IDE, or compile the program themselves.
Normal settings are stored in `%LOCALAPPDATA%\Guard Center\Portable\State\Shared\settings.ini` and remain available if the exe is updated or moved. Extracted files from different versions may currently remain in that folder.

Special features such as UAC Guard may still require suitable hardware, drivers, additional components, and user authorization.
There is currently no public GitHub Release or installer.

---

# Which features require administrator privileges?

Guard Center itself **does not need to run as administrator all the time**.

General features run with standard user privileges.

These actions may require elevation:

- Some Device Guard repairs
- UAC Guard installation/repair
- UAC Guard Protected Host management
- VSR driver-level settings
- Some PnP device operations
- Launching apps through gsudo in Link Guard

Guard Center requests Windows UAC only when needed.

---

# Important usage principles

Guard Center is designed to avoid scanning the entire computer constantly after launch.

Most expensive operations run only when needed.

For example:

- The app catalog loads in the background
- Display topology refreshes in response to events
- Audio sessions update only when the relevant pages need them
- Display DDC operations do not block the UI
- Game Helper scans All apps only when it is first opened
- Link Guard loads the application catalog only when needed

Leaving Guard Center in the system tray therefore does not mean it continuously runs every hardware scan.

---

# Troubleshooting

## An app's audio session is missing

Play audio from the app once, then return to Audio Guard.

## Brightness / Contrast is missing in Display Guard

Possible reasons:

- The monitor does not support DDC/CI
- DDC/CI is disabled in the monitor's on-screen display (OSD)
- The driver does not expose the feature
- The display has just resumed from sleep
- A dock or adapter blocks DDC commands

Try **Refresh** and make sure DDC/CI is enabled in the monitor's OSD.

## VSR Guard reports NVIDIA GPU Unsupported

Check that:

- You are using an NVIDIA RTX GPU
- The NVIDIA driver is working
- The GPU is currently enabled

## Chrome Graphics Acceleration cannot be changed

If Chrome is running, it may write its own Local State back over the change.

First:

1. Close every Chrome window
2. Confirm that all Chrome processes have exited
3. Change the setting again

## UAC Guard cannot find Codex

UAC Guard's Codex Process Mode needs to find the **OpenAI Codex Windows App** and its `app-server` process.

If Codex has not started, UAC Guard waits for it.

## Device Guard still reports a problem after repair

Some driver/PnP problems cannot be fixed by restarting the device alone.

You may still need to:

- Restart Windows
- Disconnect and reconnect the device
- Update the driver
- Reinstall the driver
- Use an OEM utility

Device Guard tries to show the corresponding state, such as `Restart required` or `Driver required`.

---

# Technical architecture

Main structure:

```text
Guard-Center
│
├─ README.md, build.bat, Guard Center.exe (local build artifact; not tracked by Git)
├─ src
│  ├─ Guard Center.csproj, App.xaml, App.xaml.cs
│  ├─ Modules
│  ├─ Shared
│  ├─ Tools
│  │  ├─ UacGuardHost
│  │  └─ PortableLauncher
│  ├─ GuardCenter.Tests
│  └─ Assets
└─ dist (build output; not tracked by Git)
```

The main program and UI run in a standard user context.

Only specific operations that require administrator privileges use a separate elevated host or gsudo.

---

# Project

Repository:

https://github.com/daniel88516/Guard-Center

Guard Center aims to bring Windows features that you regularly adjust, monitor, or repair into one interface, reducing the need to switch between Control Panel, Settings pages, and third-party tools.
