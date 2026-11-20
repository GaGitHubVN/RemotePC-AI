import 'package:flutter/material.dart';

import '../app_state.dart';
import '../l10n.dart';
import 'home.dart';

class SettingsScreen extends StatelessWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final s = AppState.instance;
    return Scaffold(
      appBar: AppBar(title: Text(tr('settings'))),
      body: ListenableBuilder(
        listenable: s,
        builder: (context, _) => ListView(children: [
          ListTile(
            leading: const Icon(Icons.language),
            title: Text(tr('language')),
            trailing: SegmentedButton<String>(
              segments: const [ButtonSegment(value: 'vi', label: Text('VI')), ButtonSegment(value: 'en', label: Text('EN'))],
              selected: {s.language},
              onSelectionChanged: (v) { s.language = v.first; s.savePrefs(); },
            ),
          ),
          ListTile(
            leading: const Icon(Icons.dark_mode_outlined),
            title: Text(tr('theme')),
            trailing: DropdownButton<String>(
              value: s.themeMode,
              items: const [DropdownMenuItem(value: 'system', child: Text('System')), DropdownMenuItem(value: 'light', child: Text('Light')), DropdownMenuItem(value: 'dark', child: Text('Dark'))],
              onChanged: (v) { s.themeMode = v!; s.savePrefs(); },
            ),
          ),
          SwitchListTile(secondary: const Icon(Icons.animation), title: Text(tr('reduce_motion')), value: s.reduceMotion, onChanged: (v) { s.reduceMotion = v; s.savePrefs(); }),
          SwitchListTile(secondary: const Icon(Icons.touch_app_outlined), title: Text(tr('trackpad')), value: s.trackpadMode, onChanged: (v) { s.trackpadMode = v; s.savePrefs(); }),
          ListTile(
            leading: const Icon(Icons.speed),
            title: Text(tr('sensitivity')),
            subtitle: Slider(min: 0.5, max: 3, divisions: 10, value: s.pointerSensitivity, label: s.pointerSensitivity.toStringAsFixed(1),
                onChanged: (v) { s.pointerSensitivity = v; s.savePrefs(); }),
          ),
          const Divider(),
          ListTile(leading: const Icon(Icons.shield_outlined), title: Text(tr('security')), onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const SecurityScreen()))),
          ListTile(leading: const Icon(Icons.info_outline), title: Text(tr('about')), onTap: () => showAboutDialog(
                context: context,
                applicationName: 'RemotePC AI',
                applicationVersion: '0.1.0',
                children: const [Text('Remote desktop and local AI for your own Windows PC. End-to-end encrypted; AI runs on your PC.')],
              )),
        ]),
      ),
    );
  }
}

class SecurityScreen extends StatelessWidget {
  const SecurityScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final s = AppState.instance;
    return Scaffold(
      appBar: AppBar(title: Text(tr('security'))),
      body: ListView(children: [
        ListTile(title: const Text('This phone\'s device ID'), subtitle: SelectableText(s.keys.deviceId)),
        const ListTile(
          title: Text('How your connection is protected'),
          subtitle: Text('Keys are stored in the Keychain / Android Keystore. Signaling messages are end-to-end encrypted (X25519 + AES-256-GCM); '
              'video and data use WebRTC DTLS-SRTP. The PC checks permissions for every request.'),
        ),
        const Divider(),
        for (final h in s.hosts)
          ListTile(
            leading: const Icon(Icons.computer),
            title: Text(h.name),
            subtitle: Text(h.permissions.join(', ')),
            onTap: () => Navigator.push(context, MaterialPageRoute(builder: (_) => HostDetailsScreen(host: h))),
          ),
        const Divider(),
        ListTile(
          leading: Icon(Icons.restart_alt, color: Theme.of(context).colorScheme.error),
          title: Text(tr('reset_identity'), style: TextStyle(color: Theme.of(context).colorScheme.error)),
          subtitle: const Text('Deletes this phone\'s keys and all paired PCs. You will need to pair again.'),
          onTap: () async {
            final ok = await showDialog<bool>(context: context, builder: (c) => AlertDialog(
                  title: Text(tr('reset_identity')),
                  actions: [TextButton(onPressed: () => Navigator.pop(c, false), child: Text(tr('cancel'))), FilledButton(onPressed: () => Navigator.pop(c, true), child: const Text('OK'))],
                ));
            if (ok == true) await s.resetIdentity();
          },
        ),
      ]),
    );
  }
}
