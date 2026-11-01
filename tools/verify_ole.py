import pathlib,json,io,zipfile,xml.etree.ElementTree as E,sys,math
root=pathlib.Path(sys.argv[1]).resolve(); folder=root/'ole-v1'
import olefile
from openpyxl.formula.translate import Translator
ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
def payload(path):
    lines=path.read_text(encoding='utf-8').splitlines(); pairs=list(zip(lines[::2],lines[1::2]))
    a=next(i for i,(k,v) in enumerate(pairs) if k.strip()=='0' and v=='OLE2FRAME')
    b=next(i for i in range(a+1,len(pairs)) if pairs[i][0].strip()=='0')
    return bytes.fromhex(''.join(v for k,v in pairs[a:b] if k.strip()=='310'))
def workbook(data):
    obj=olefile.OleFileIO(io.BytesIO(data[128:])); package=obj.openstream('Package').read(); z=zipfile.ZipFile(io.BytesIO(package))
    wb=E.fromstring(z.read('xl/workbook.xml')); tab=int(wb.find('s:bookViews/s:workbookView',ns).attrib['activeTab'])
    ss=[]
    if 'xl/sharedStrings.xml' in z.namelist(): ss=[''.join(i.itertext()) for i in E.fromstring(z.read('xl/sharedStrings.xml'))]
    sheets={}
    for name in z.namelist():
        if name.startswith('xl/worksheets/sheet') and name.endswith('.xml'):
            cells={}; shared={}
            elements=E.fromstring(z.read(name)).findall('.//s:c',ns)
            for c in elements:
                f=c.find('s:f',ns)
                if f is not None and f.attrib.get('t')=='shared' and f.text: shared[f.attrib['si']]=(c.attrib['r'],f.text)
            for c in elements:
                v=c.find('s:v',ns); f=c.find('s:f',ns); typ=c.attrib.get('t','n'); value=v.text if v is not None else None
                formula=f.text if f is not None else None
                if f is not None and f.attrib.get('t')=='shared' and not formula:
                    origin,source=shared[f.attrib['si']]; formula=Translator('='+source,origin=origin).translate_formula(c.attrib['r'])[1:]
                if typ=='s': value=ss[int(value)]
                elif typ=='inlineStr': value=''.join(c.find('s:is',ns).itertext()); typ='s'
                cells[c.attrib['r']]={'t':typ,'v':value,'f':formula}
            sheets[name]=cells
    return obj,package,tab,sheets
plans={p['File']:p for p in json.loads((folder/'preview_plan_refined.json').read_text(encoding='utf-8-sig'))}
objects=json.loads((folder/'batch_objects.json').read_text(encoding='utf-8-sig')); reports=[]; (folder/'translated').mkdir(exist_ok=True)
for item in objects:
    key=item['Key']; data=payload(folder/f'native_verify_{key}.dxf'); old=payload(folder/f'{key}.dxf')
    obj,package,tab,sheets=workbook(data); _,_,oldtab,oldsheets=workbook(old)
    active=f'xl/worksheets/sheet{tab+1}.xml'; cells=sheets[active]; changes={e['Handle']:e['Target'] for e in plans[key]['Entries'] if e['Target']!=e['Source']}
    errors=[]; formulas=0; numbers=0
    for sheet,oldcells in oldsheets.items():
        newcells=sheets[sheet]
        for addr,before in oldcells.items():
            after=newcells.get(addr,{})
            if before['f'] is not None:
                formulas+=1
                if after.get('f')!=before['f']: errors.append(f'formula:{sheet}:{addr}')
                if before['t']=='n' and before['v'] is not None and (after.get('v') is None or not math.isclose(float(before['v']),float(after['v']),rel_tol=1e-12,abs_tol=1e-9)): errors.append(f'formula-result:{sheet}:{addr}')
            elif before['t']=='n' and before['v'] is not None:
                numbers+=1
                if after.get('v') is None or not math.isclose(float(before['v']),float(after['v']),rel_tol=1e-12,abs_tol=1e-9): errors.append(f'number:{sheet}:{addr}')
            elif before['t']=='s':
                expected=changes.get(addr,before['v']) if sheet==active else before['v']
                if after.get('v')!=expected: errors.append(f'text:{sheet}:{addr}')
    for addr,target in changes.items():
        if cells.get(addr,{}).get('v')!=target: errors.append(f'target:{addr}')
    pres=obj.openstream('\x02OlePres000').read(); (folder/'translated'/f'{key}_cache.bin').write_bytes(pres)
    (folder/'translated'/f'{key}_verified.xlsx').write_bytes(package)
    reports.append({'Key':key,'Handle':item['Handle'],'ChangedCells':len(changes),'Errors':errors,'GeometryPreserved':data[:124]==old[:124],'ActiveTabPreserved':tab==oldtab,'FormulasCheckedAllSheets':formulas,'NumbersCheckedAllSheets':numbers,'CacheBytes':len(pres)})
(folder/'verified.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(reports,ensure_ascii=False,indent=2)); assert all(not x['Errors'] and x['GeometryPreserved'] and x['ActiveTabPreserved'] for x in reports)
