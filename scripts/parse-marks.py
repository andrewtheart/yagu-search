import json, sys, struct

if len(sys.argv) < 2:
    sys.exit('Usage: python parse-marks.py <path-to-.counters-file>')
path = sys.argv[1]
data = open(path, 'rb').read()
text = data.decode('utf-16-le', errors='replace')
if text.startswith('\ufeff'):
    text = text[1:]

obj = json.loads(text)

# Empirically determined: inter-sample deltas are ~10^9 for 1s intervals
# The stated qpcFrequency of 10^7 doesn't match the data
TICK_RATE = 1_000_000_000  # 1 GHz

def to_u64(t_obj):
    return (t_obj['h'] << 32) | (t_obj['l'] & 0xFFFFFFFF)

# Get the first data point time as t=0 reference
first_qpc = None
for c in obj['counters']:
    if c['p']:
        qpc = to_u64(c['p'][0]['t'])
        if first_qpc is None or qpc < first_qpc:
            first_qpc = qpc

def qpc_to_seconds(t_obj):
    return (to_u64(t_obj) - first_qpc) / TICK_RATE

# Find UserMarks
mark_times = []
for c in obj['counters']:
    if 'UserMark' in c['id']:
        print(f'=== User Marks ({len(c["p"])} marks) ===')
        for p in c['p']:
            elapsed_s = qpc_to_seconds(p['t'])
            mark_times.append(elapsed_s)
            print(f'  MARK at t={elapsed_s:.1f}s')

# Key perf counters - print all datapoints focusing around marks
print('\n--- Counter data around user marks ---')
interesting = {
    'gc-heap-size': 'GC Heap (MB)',
    'working-set': 'Working Set (MB)',
    'threadpool-thread-count': 'TP Threads',
    'cpu-usage': 'CPU %',
    'alloc-rate': 'Alloc Rate (B/s)',
    'gen-2-gc-count': 'Gen2 GC Count',
    'time-in-gc': 'Time in GC %',
    'loh-size': 'LOH Size (MB)',
}

for c in obj['counters']:
    cid = c['id'].lower()
    for name, label in interesting.items():
        if name in cid and '.dotnet.' not in cid:  # prefer EventCounter versions
            print(f'\n{label}: ({c["id"]}, {len(c["p"])} pts)')
            points = c['p']
            for i, p in enumerate(points):
                t = qpc_to_seconds(p['t'])
                val = p.get('v', 'N/A')
                # Scale MB for working-set (bytes) and heap size (bytes in MB already for EventCounter)
                vstr = f'{val}'
                if 'working-set' in name:
                    vstr = f'{val:.0f} MB'  # EventCounter reports in MB
                elif 'heap-size' in name or 'loh-size' in name:
                    vstr = f'{val:.0f} MB'
                elif 'cpu-usage' in name:
                    vstr = f'{val:.1f}%'
                elif 'time-in-gc' in name:
                    vstr = f'{val:.1f}%'
                elif 'alloc-rate' in name:
                    vstr = f'{val:.0f} B/s'
                    
                # Print if near a mark (±15s) or every 10th point
                near_mark = any(abs(t - mt) < 15 for mt in mark_times)
                if near_mark or i % 20 == 0 or i < 3 or i >= len(points) - 3:
                    marker = ' <<<MARK' if any(abs(t - mt) < 1.5 for mt in mark_times) else ''
                    print(f'  [{i:3d}] t={t:7.1f}s  {vstr}{marker}')
            break

# Also look at PrivateBytes (higher frequency)
for c in obj['counters']:
    if 'PrivateBytes' in c['id']:
        print(f'\nPrivateBytes (high freq, {len(c["p"])} pts) - around marks:')
        for p in c['p']:
            t = qpc_to_seconds(p['t'])
            val = p.get('v', 0)
            near_mark = any(abs(t - mt) < 10 for mt in mark_times)
            if near_mark:
                marker = ' <<<MARK' if any(abs(t - mt) < 1.5 for mt in mark_times) else ''
                print(f'  t={t:7.1f}s  {val/1024/1024:.0f} MB{marker}')

# Process CPU (high freq)
for c in obj['counters']:
    if 'Process.CPU' in c['id']:
        print(f'\nProcess CPU (high freq, {len(c["p"])} pts) - around marks:')
        for p in c['p']:
            t = qpc_to_seconds(p['t'])
            val = p.get('v', 0)
            near_mark = any(abs(t - mt) < 10 for mt in mark_times)
            if near_mark:
                marker = ' <<<MARK' if any(abs(t - mt) < 1.5 for mt in mark_times) else ''
                print(f'  t={t:7.1f}s  {val:.1f}%{marker}')
