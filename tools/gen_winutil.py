# Generates src/WinTune/Core/WinUtilTweaks.cs from the winutil data (config/tweaks.json, MIT license).
# Usage:  python gen_winutil.py tweaks.json ..\src\WinTune\Core\WinUtilTweaks.cs
# tweaks.json is downloaded from https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json
# To add an entry: one line in ITEMS (winutil key, id, category, title, description, kind, direction, options).
# Entries that are not made only of registry values are in WinUtilExtras.cs, written by hand.
import json, sys

src, out = sys.argv[1], sys.argv[2]
d = json.loads(open(src, encoding='utf-8-sig').read(), strict=False)

GAME, PRIV, EXPL, TASKBAR, APPR, PWR, SYS, NETWORK, BROW = ['TweakCatalog.' + n for n in
    ('Gaming', 'Privacy', 'FileExplorer', 'Taskbar', 'Appearance', 'Power', 'SystemCategory', 'Network', 'Browser')]
G = {'f': ('Enabled', 'Disabled'), 'm': ('Enabled', 'Disabled'), 'fp': ('Enabled', 'Disabled'), 'mp': ('Enabled', 'Disabled')}

# winutil key, id, category, title, description, kind, on = applied value?, options
ITEMS = [
 # ---------- Gaming and performance ----------
 ('WPFTweaksDisplay', 'system.visualEffects', GAME, 'Windows visual effects', "Animations, shadows and previews. When disabled, Windows uses the \"best performance\" setting.", 'mp', False,
   {'restart': True,
    'apply': '() => RegistryUtil.SetBinary(RegistryHive.CurrentUser, @"Control Panel\\Desktop", "UserPreferencesMask", new byte[] { 144, 18, 3, 128, 16, 0, 0, 0 })',
    'undo': '() => RegistryUtil.DeleteValue(RegistryHive.CurrentUser, @"Control Panel\\Desktop", "UserPreferencesMask")'}),
 ('WPFTweaksDisableBGapps', 'system.appInBackground', GAME, 'Microsoft Store apps in the background', "Lets Microsoft Store apps stay active even when they are closed.", 'fp', False, {}),

 # ---------- Privacy and telemetry ----------
 ('WPFTweaksTelemetry', 'privacy.fullTelemetry', PRIV, 'Telemetry and data collection (full package)', "Turns off together: diagnostic data, advertising ID, tailored experiences, online speech recognition, collection of what you type, feedback requests, sample submission to Defender, and the telemetry and error reporting services. It also changes some of the items next to it.", 'f', False,
   {'apply': 'WinUtilExtras.ApplyTelemetryExtras', 'undo': 'WinUtilExtras.UndoTelemetryExtras'}),
 ('WPFTweaksLocation', 'privacy.location', PRIV, 'Location', "Location access for Windows and apps (weather, maps, Find my device).", 'f', False, {}),
 ('WPFTweaksConsumerFeatures', 'privacy.sponsoredApps', PRIV, 'Sponsored apps and promotional content', "Automatic installation of promoted apps and suggestions from the Microsoft Store.", 'fp', False, {'rec': 'false'}),
 ('WPFTweaksPreventDeviceMetadataFromNetwork', 'privacy.deviceApps', PRIV, 'Device apps and info from the Internet', "When you connect a device, Windows downloads the manufacturer icons, information and apps.", 'fp', False, {}),
 ('WPFTweaksWindowsAI', 'privacy.windowsAi', PRIV, 'Windows AI features (Copilot, Recall)', "When you turn them off, WinTune removes Copilot, the Microsoft 365 Copilot app and the AI components, and turns off Recall and the Notepad AI features. Turning the switch back on restores only the settings: removed apps are reinstalled from the Microsoft Store.", 'fp', False,
   {'w11': True, 'apply': 'WinUtilExtras.RemoveWindowsAi',
    'confirm': 'value => value == "false" ? "Copilot, the Microsoft 365 Copilot app and the Windows AI components will be removed, and Recall will be turned off. A PowerShell window opens to show the progress.\\n\\nRemoved apps do not come back by themselves: they are reinstalled from the Microsoft Store. Continue?" : null'}),

 # ---------- File Explorer ----------
 ('WPFTweaksRemoveHomeAndGallery', 'explorer.homeGallery', EXPL, 'Home and Gallery in File Explorer', "\"Home\" and \"Gallery\" entries in the side pane. When disabled, File Explorer opens on \"This PC\".", 'fp', False,
   {'explorer': True, 'w11': True, 'skip': ['LaunchTo'],
    'apply': '() => RegistryUtil.SetDword(RegistryHive.CurrentUser, @"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", "LaunchTo", 1)'}),
 ('WPFToggleLongPaths', 'system.longPaths', EXPL, 'Paths longer than 260 characters', "Long path support for programs that use it.", 'mp', True, {}),

 # ---------- Taskbar and Start ----------
 ('WPFToggleTaskbarAlignment', 'taskbar.centeredIcons', TASKBAR, 'Centered taskbar icons', "When disabled, the taskbar icons are aligned to the left.", 'fp', True, {'w11': True}),
 ('WPFToggleTaskbarSearch', 'taskbar.searchIcon', TASKBAR, 'Search icon on the taskbar', "Search button on the taskbar.", 'f', True, {}),
 ('WPFToggleTaskView', 'taskbar.taskView', TASKBAR, 'Task View button', "Virtual desktops button on the taskbar.", 'm', True, {}),
 ('WPFTweaksEndTaskOnTaskbar', 'taskbar.endTask', TASKBAR, '"End task" on right-click', "Adds \"End task\" to the menu that appears when you right-click a program on the taskbar.", 'm', True, {'w11': True}),
 ('WPFToggleBatteryPercentage', 'taskbar.batteryPercentage', TASKBAR, 'Battery percentage on the taskbar', "Shows the number next to the battery icon. Laptops only.", 'f', True, {}),
 ('WPFTweaksDisableNotifications', 'taskbar.notifications', TASKBAR, 'Notifications and notification center', "When turned off, all notifications disappear, and so does the calendar next to the clock.", 'fp', False, {'explorer': True}),
 ('WPFToggleStartMenuRecommendations', 'taskbar.startRecommended', TASKBAR, '"Recommended" section of the Start menu', "Turning it off also turns off Windows Spotlight on the lock screen.", 'f', True, {'explorer': True, 'w11': True}),
 ('WPFTweaksRevertStartMenu', 'taskbar.classicStart', TASKBAR, 'Old Start menu layout', "Brings the Start menu back to how it was before the new Windows 11 25H2 layout. It has no effect on the most recent versions of Windows.", 'm', True, {'explorer': True, 'w11': True}),

 # ---------- Appearance and accessibility ----------
 ('WPFToggleDarkMode', 'system.darkTheme', APPR, 'Dark theme', "Dark theme for Windows and apps.", 'm', True, {}),
 ('WPFToggleWindowSnapping', 'system.snap', APPR, 'Window snapping', "Windows snap to the screen edges when you drag them.", 'm', True, {}),
 ('WPFToggleScrollbars', 'system.scrollBars', APPR, 'Always show scroll bars', "When disabled, Windows hides the scroll bars when they are not needed.", 'fp', True, {}),
 ('WPFToggleStickyKeys', 'system.stickyKeys', APPR, 'Sticky Keys', "The feature that turns on when you press Shift five times in a row.", 'mp', True, {}),
 ('WPFToggleNumLock', 'system.numLock', APPR, 'Num Lock on at startup', "State of the Num Lock key when the PC starts.", 'm', True, {}),
 ('WPFToggleDisableLockscreen', 'system.lockScreen', APPR, 'Lock screen', "When disabled, startup and wake-up go straight to the sign-in screen.", 'f', False, {}),
 ('WPFToggleLoginBlur', 'system.signInBlur', APPR, 'Blur on the sign-in screen', "Blur effect on the sign-in screen background.", 'f', True, {}),
 ('WPFToggleHideSettingsHome', 'system.homeSettings', APPR, 'Home page in Settings', "The start page of the Settings app.", 'f', True, {'w11': True}),

 # ---------- Power ----------
 ('WPFToggleStandbyFix', 'power.networkInStandby', PWR, 'Network active during Modern Standby', "Keeps the network connection while the PC is in Modern Standby (S0), typical of recent laptops.", 'f', True, {}),
 ('WPFToggleS3Sleep', 'power.sleepS3', PWR, 'S3 sleep instead of Modern Standby', "Uses classic sleep (S3), which powers down the CPU, instead of Modern Standby. It only works if the PC supports it.", 'f', True, {'restart': True}),

 # ---------- System ----------
 ('WPFToggleDetailedBSoD', 'system.blueScreenDetails', SYS, 'Blue screen with technical details', "On a crash, shows the error parameters instead of the sad face.", 'f', True, {}),
 ('WPFToggleVerboseLogon', 'system.startupMessages', SYS, 'Verbose startup messages', "Shows what Windows is doing during startup, shutdown, sign-in and sign-out.", 'mp', True, {}),
 ('WPFTweaksStorage', 'system.memorySensor', SYS, 'Storage Sense', "Automatically deletes temporary files when disk space runs low.", 'm', False, {}),
 ('WPFTweaksWPBT', 'system.wpbt', SYS, 'Manufacturer programs at startup (WPBT)', "Lets the PC or motherboard manufacturer run programs when Windows starts, for example to install its utilities.", 'mp', False, {'restart': True}),
 ('WPFTweaksRazerBlock', 'system.razerSoftware', SYS, 'Automatic Razer software installation', "When disabled, connecting a Razer device does not start the Synapse installation. Note: this also turns off the automatic driver search on Windows Update when you connect a new device.", 'f', False,
   {'apply': 'WinUtilExtras.BlockRazerFolder', 'undo': 'WinUtilExtras.UnblockRazerFolder'}),
 ('WPFTweaksUTC', 'system.utcClock', SYS, 'Hardware clock in UTC', "Only useful when dual booting with Linux: avoids the wrong time when switching between systems.", 'm', True, {'restart': True}),
 ('WPFTweaksDisableWarningForUnsignedRdp', 'system.rdpWarnings', SYS, 'Warnings for unsigned RDP files', "Warning shown when you open an unsigned Remote Desktop file (.rdp).", 'mp', False, {}),
 ('WPFToggleNewOutlook', 'system.newOutlook', SYS, 'New Outlook', "Uses the new Outlook app instead of the classic Office one.", 'm', True, {}),

 # ---------- Network ----------
 ('WPFTweaksDeliveryOptimization', 'network.updateSharing', NETWORK, 'Sending updates to other PCs', "Delivery Optimization: Windows uses your connection to send updates to other PCs, on the local network or over the Internet.", 'm', False, {}),
 ('WPFTweaksIPv46', 'network.preferIpv4', NETWORK, 'Prefer IPv4 over IPv6', "Can reduce latency on networks where IPv6 is not configured. IPv6 stays enabled.", 'm', True, {'restart': True}),

 # ---------- Browser ----------
 ('WPFTweaksBraveDebloat', 'browser.braveExtra', BROW, 'Brave: Rewards, Wallet, VPN, Leo AI and statistics', "When disabled, Brave hides Rewards, Wallet, VPN, Leo AI, News, Talk and Tor and sends no statistics. The settings will show \"managed by your organization\".", 'fp', False, {}),
 ('WPFTweaksEdgeDebloat', 'browser.edgeExtra', BROW, 'Edge: telemetry, recommendations and promotions', "When disabled, Edge sends no diagnostic data and shows no recommendations, Rewards, shopping or promotional screens.", 'mp', False, {}),
]

HIVES = {'HKLM': 'RegistryHive.LocalMachine', 'HKCU': 'RegistryHive.CurrentUser', 'HKU': 'RegistryHive.Users'}
KINDS = {'DWord': 'RegKind.DWord', 'QWord': 'RegKind.QWord', 'String': 'RegKind.String'}

def verb(s): return '@"' + s.replace('"', '""') + '"'
def norm(s): return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'
def val(v): return 'null' if v is None or v == '<RemoveEntry>' else norm(str(v))

lines = []
w = lines.append
w('// Entries derived from "Chris Titus Tech\'s Windows Utility" (https://github.com/ChrisTitusTech/winutil),')
w('// file config/tweaks.json. MIT license, Copyright (c) 2022 CT Tech Group LLC.')
w('// File generated by tools/gen_winutil.py: titles and descriptions are translated, the registry values are the original ones.')
w('using System.Collections.Generic;')
w('using Microsoft.Win32;')
w('')
w('namespace WinTune.Core;')
w('')
w('/// <summary>Optimizations taken from the Chris Titus Tech Windows Utility, as switches with a readable state that can be undone.</summary>')
w('internal static class WinUtilTweaks')
w('{')
w('    public static List<Tweak> Build() => new()')
w('    {')
ids = set()
for key, tid, cat, title, desc, g, on_applied, opt in ITEMS:
    assert tid not in ids, tid
    ids.add(tid)
    t = d[key]
    on, off = G[g]
    w('        RegistryBundle.Toggle(')
    w('            id: ' + norm(tid) + ',')
    w('            category: ' + cat + ',')
    w('            title: ' + norm(title) + ',')
    w('            description: ' + norm(desc) + ',')
    w('            onText: ' + norm(on) + ',')
    w('            offText: ' + norm(off) + ',')
    w('            onIsApplied: ' + ('true' if on_applied else 'false') + ',')
    if opt.get('rec'): w('            recommended: ' + norm(opt['rec']) + ',')
    if opt.get('restart'): w('            needsRestart: true,')
    if opt.get('explorer'): w('            needsExplorerRestart: true,')
    if opt.get('w11'): w('            windows11Only: true,')
    if opt.get('apply'): w('            afterApply: ' + opt['apply'] + ',')
    if opt.get('undo'): w('            afterUndo: ' + opt['undo'] + ',')
    if opt.get('confirm'): w('            confirm: ' + opt['confirm'] + ',')
    w('            entries: new RegEntry[]')
    w('            {')
    n = 0
    for r in t['registry']:
        if r['Name'] in opt.get('skip', []): continue
        hive, rest = r['Path'].split(':\\', 1)
        ds = r.get('DefaultState')
        default = 'null' if ds is None else ('true' if str(ds).lower() == 'true' else 'false')
        w('                new(%s, %s, %s, %s, %s, %s, %s),' % (HIVES[hive], verb(rest), norm(r['Name']), KINDS[r['Type']], val(r.get('Value')), val(r.get('OriginalValue')), default))
        n += 1
    assert n > 0, key
    w('            }),')
w('    };')
w('}')
open(out, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
print('items:', len(ITEMS))
