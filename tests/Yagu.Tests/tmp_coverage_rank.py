import xml.etree.ElementTree as ET
root = ET.parse('TestResults/coverage.cobertura.xml').getroot()
rows = []
for cls in root.findall('.//class'):
    filename = cls.get('filename', '')
    if not (filename.startswith('..\\Yagu\\') or filename.startswith('../Yagu/') or filename.startswith('Yagu\\') or filename.startswith('Yagu/')):
        continue
    rows.append((float(cls.get('line-rate', '0')), float(cls.get('branch-rate', '0')), cls.get('name', ''), filename, int(cls.get('lines', '0')), int(cls.get('methods', '0'))))
for item in sorted(rows, key=lambda x: (x[0], x[1], x[2]))[:120]:
    print(f"{item[0]:.3f} line  {item[1]:.3f} branch  {item[2]:40}  {item[3]}  lines={item[4]} methods={item[5]}")
