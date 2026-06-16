import xml.etree.ElementTree as ET
from pathlib import Path

path = Path('Yagu.Tests/TestResults/coverage.cobertura.xml')
root = ET.parse(path).getroot()
classes = []
for cls in root.findall('.//class'):
    filename = cls.get('filename', '')
    name = cls.get('name', '')
    is_repo_file = any(token in filename for token in ('QuickGrep/', 'QuickGrep\\', 'Yagu/', 'Yagu\\', 'Yagu.Tests/', 'Yagu.Tests\\'))
    if not is_repo_file:
        continue
    if 'Test' in name or 'Generated' in filename or 'obj/' in filename:
        continue
    classes.append({
        'line_rate': float(cls.get('line-rate', '0')),
        'branch_rate': float(cls.get('branch-rate', '0')),
        'complexity': int(cls.get('complexity', '0')),
        'filename': filename,
        'name': name,
    })
classes = sorted(classes, key=lambda x: (x['line_rate'], x['branch_rate'], x['complexity']))
print('TOTAL_CLASSES', len(classes))
for item in classes[:150]:
    print(f"{item['line_rate']:.3f} line | {item['branch_rate']:.3f} branch | {item['complexity']:2d} cplx | {item['filename']} | {item['name']}")
