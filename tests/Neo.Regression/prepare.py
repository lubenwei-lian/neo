"""Extract exact production method bodies; mock only I/O and scheduling boundaries."""
from pathlib import Path
import hashlib
import json

here = Path(__file__).resolve().parent
root = here.parents[1]
source = (root / 'src/Neo.App/Services/ConnectionService.cs').read_text(encoding='utf-8-sig')
signatures = [
    '    private static async Task RunPostConnectSelfCheckAsync(',
    '    private static async Task<bool> TryAutoRestartCoreAsync(',
    '    private static async Task WatchdogLoopAsync(',
]
methods = []
for signature in signatures:
    start = source.index(signature)
    end = source.index('\n    }\n', start) + len('\n    }')
    methods.append(source[start:end])
prefix = (here / 'PolicyHarness.txt').read_text(encoding='utf-8')
(here / 'PolicyUnderTest.g.cs').write_text(prefix + '\n'.join(methods) + '\n}\n', encoding='utf-8')
(here / 'source-methods.json').write_text(json.dumps({
    'source': 'src/Neo.App/Services/ConnectionService.cs',
    'bodies': [{'signature': sig.strip(), 'sha256': hashlib.sha256(method.encode()).hexdigest()} for sig, method in zip(signatures, methods)],
    'boundary_changes': 'Watchdog interval is 0.005s; retry and self-check delays are unchanged. OS/network/connect boundaries are fake.',
}, indent=2), encoding='utf-8')
subs = (root / 'src/Neo.App/Services/AppState.cs').read_text(encoding='utf-8-sig')
methods = []
for name in ['RefreshSubscriptionAsync()', 'RefreshOneAsync(string id)']:
    start = subs.index('    public static async Task<(bool Ok, string? Error)> ' + name)
    end = subs.index('\n    }\n', start) + len('\n    }')
    methods.append(subs[start:end])
(here / 'SubscriptionUnderTest.g.cs').write_text((here / 'SubscriptionHarness.txt').read_text(encoding='utf-8') + '\n'.join(methods) + '\n}\n', encoding='utf-8')
facts_path = here / 'source-methods.json'
facts = json.loads(facts_path.read_text(encoding='utf-8'))
facts['subscription_bodies'] = [{'sha256': hashlib.sha256(method.encode()).hexdigest(), 'signature': method.split('\n', 1)[0].strip()} for method in methods]
facts_path.write_text(json.dumps(facts, indent=2), encoding='utf-8')
