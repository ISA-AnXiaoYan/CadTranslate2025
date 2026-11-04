"""Prepare and verify all-sheet translations for qualified DWG XLSX OLE objects."""

import argparse
import copy
import hashlib
import io
import json
import math
import os
from pathlib import Path
import posixpath
import re
import shutil
import subprocess
import zipfile

from lxml import etree
import olefile
from openpyxl.formula.translate import Translator


XML_NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
REL_NS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
CFB_SIGNATURE = bytes.fromhex("D0CF11E0A1B11AE1")
FONT = "Microsoft YaHei"


def tag(name):
    return f"{{{XML_NS}}}{name}"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def payload(path):
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    pairs = list(zip(lines[::2], lines[1::2]))
    start, = (i for i, (code, value) in enumerate(pairs)
              if code.strip() == "0" and value.strip() == "OLE2FRAME")
    end = next(i for i in range(start + 1, len(pairs)) if pairs[i][0].strip() == "0")
    data = bytes.fromhex("".join(value for code, value in pairs[start:end] if code.strip() == "310"))
    if data[128:136] != CFB_SIGNATURE:
        raise ValueError(f"Unsupported OLE payload layout in {path.name}")
    return data


def workbook_parts(storage):
    with olefile.OleFileIO(io.BytesIO(storage)) as container:
        return (container.openstream("Package").read(),
                container.openstream("\x02OlePres000").read())


def xml(archive, name):
    parser = etree.XMLParser(resolve_entities=False, no_network=True)
    return etree.fromstring(archive.read(name), parser)


def text_value(element):
    # Exclude phonetic guides from both shared strings and inline rich text.
    texts = element.findall(tag("t")) + element.findall(f"{tag('r')}/{tag('t')}")
    return "".join(item.text or "" for item in texts)


class Workbook:
    def __init__(self, package):
        self.archive = zipfile.ZipFile(io.BytesIO(package))
        parts = self.archive.namelist()
        if any(name.startswith("xl/externalLinks/") or name.endswith("vbaProject.bin") for name in parts):
            raise ValueError("Macro-enabled and externally linked workbooks are outside the supported profile")
        book = xml(self.archive, "xl/workbook.xml")
        view = book.find(f"{tag('bookViews')}/{tag('workbookView')}")
        active_index = int(view.get("activeTab", "0")) if view is not None else 0
        relationships = {item.get("Id"): item for item in xml(self.archive, "xl/_rels/workbook.xml.rels")}
        self.sheets = {}
        self.states = {}
        for sheet in book.find(tag("sheets")):
            relationship = relationships[sheet.get(f"{{{REL_NS}}}id")]
            if not relationship.get("Type", "").endswith("/worksheet"):
                raise ValueError("Only worksheet parts are supported")
            target = relationship.get("Target")
            part = posixpath.normpath(target.lstrip("/") if target.startswith("/") else "xl/" + target)
            if not part.startswith("xl/worksheets/"):
                raise ValueError("Unexpected worksheet part")
            self.sheets[sheet.get("name")] = part
            self.states[sheet.get("name")] = sheet.get("state", "visible")
        self.active = list(self.sheets)[active_index]
        self.strings = ([text_value(item) for item in xml(self.archive, "xl/sharedStrings.xml")]
                        if "xl/sharedStrings.xml" in parts else [])
        self.styles = xml(self.archive, "xl/styles.xml")
        self.fonts = self.styles.find(tag("fonts"))
        self.formats = self.styles.find(tag("cellXfs"))

    def cell_value(self, cell):
        kind = cell.get("t", "n")
        value = cell.findtext(tag("v"))
        if kind == "s":
            return "text", self.strings[int(value)]
        if kind == "inlineStr":
            return "text", text_value(cell.find(tag("is")))
        if kind == "str":
            return "text", value
        return kind, value

    def snapshot(self, sheet):
        tree = xml(self.archive, self.sheets[sheet])
        merges = sorted(m.get("ref") for m in tree.findall(f"{tag('mergeCells')}/{tag('mergeCell')}"))
        cells = tree.findall(f".//{tag('sheetData')}/{tag('row')}/{tag('c')}")
        shared = {}
        for cell in cells:
            formula = cell.find(tag("f"))
            if formula is not None and formula.get("t") == "shared" and formula.text:
                shared[formula.get("si")] = (cell.get("r"), formula.text)
        result = {}
        for cell in cells:
            address = cell.get("r")
            kind, value = self.cell_value(cell)
            formula = cell.find(tag("f"))
            expression = None
            if formula is not None:
                expression = formula.text or ""
                if formula.get("t") == "shared" and not expression:
                    origin, source = shared[formula.get("si")]
                    expression = Translator("=" + source, origin=origin).translate_formula(address)[1:]
            if value is None and expression is None:
                continue
            style = self.formats[int(cell.get("s", "0"))]
            font = self.fonts[int(style.get("fontId", "0"))].find(tag("name"))
            result[address] = (kind, value, expression, font.get("val") if font is not None else "")
        return merges, result


def approved_entries(plan, book):
    edits = {}
    for entry in plan["Entries"]:
        sheet, address = entry["Sheet"], entry["Handle"]
        if sheet not in book.sheets or not re.fullmatch(r"[A-Z]{1,3}[1-9]\d*", address):
            raise ValueError(f"Invalid cell address {sheet}!{address}")
        if entry.get("SheetXml", book.sheets[sheet]) != book.sheets[sheet]:
            raise ValueError(f"Worksheet part mismatch for {sheet}")
        if not isinstance(entry["Source"], str) or not isinstance(entry["Target"], str) or not entry["Target"].strip():
            raise ValueError(f"Expected a nonempty text target at {sheet}!{address}")
        if (sheet, address) in edits:
            raise ValueError(f"Duplicate edit at {sheet}!{address}")
        edits[sheet, address] = entry
    return edits


def patch_package(book, edits, output):
    font_ids, format_ids = {}, {}

    def chinese_style(old_id):
        if old_id in format_ids:
            return format_ids[old_id]
        style = copy.deepcopy(book.formats[old_id])
        font_id = int(style.get("fontId", "0"))
        if font_id not in font_ids:
            font = copy.deepcopy(book.fonts[font_id])
            for name, value in [("name", FONT), ("charset", "134")]:
                child = font.find(tag(name))
                if child is None:
                    child = etree.SubElement(font, tag(name))
                child.set("val", value)
            scheme = font.find(tag("scheme"))
            if scheme is not None:
                font.remove(scheme)
            font_ids[font_id] = len(book.fonts)
            book.fonts.append(font)
        style.set("fontId", str(font_ids[font_id]))
        style.set("applyFont", "1")
        format_ids[old_id] = len(book.formats)
        book.formats.append(style)
        return format_ids[old_id]

    changed = {}
    for sheet in {name for name, _ in edits}:
        part = book.sheets[sheet]
        tree = xml(book.archive, part)
        cells = {cell.get("r"): cell for cell in tree.findall(f".//{tag('sheetData')}/{tag('row')}/{tag('c')}")}
        for (name, address), entry in edits.items():
            if name != sheet:
                continue
            cell = cells[address]
            if cell.find(tag("f")) is not None or book.cell_value(cell) != ("text", entry["Source"]):
                raise ValueError(f"Source mismatch or protected content at {sheet}!{address}")
            if entry["Source"] == entry["Target"]:
                continue
            for child in list(cell):
                if child.tag in {tag("v"), tag("is")}:
                    cell.remove(child)
            cell.set("t", "inlineStr")
            cell.set("s", str(chinese_style(int(cell.get("s", "0")))))
            text = etree.SubElement(etree.SubElement(cell, tag("is")), tag("t"))
            text.set("{http://www.w3.org/XML/1998/namespace}space", "preserve")
            text.text = entry["Target"]
        changed[part] = etree.tostring(tree, xml_declaration=True, encoding="UTF-8")
    book.fonts.set("count", str(len(book.fonts)))
    book.formats.set("count", str(len(book.formats)))
    changed["xl/styles.xml"] = etree.tostring(book.styles, xml_declaration=True, encoding="UTF-8")
    with zipfile.ZipFile(output, "w") as target:
        for entry in book.archive.infolist():
            data = changed[entry.filename] if entry.filename in changed else book.archive.read(entry)
            target.writestr(copy.copy(entry), data)


def verify_workbook(before, after, edits):
    errors = []
    if list(before.sheets) != list(after.sheets) or before.states != after.states or before.active != after.active:
        return ["Worksheet names, order, visibility or active worksheet changed"]
    for sheet in before.sheets:
        merges, cells = before.snapshot(sheet)
        new_merges, new_cells = after.snapshot(sheet)
        if merges != new_merges:
            errors.append(f"Merge ranges changed in {sheet}")
        for name, address in edits:
            if name == sheet and address not in cells:
                errors.append(f"Missing source cell at {sheet}!{address}")
        for address in cells.keys() | new_cells.keys():
            kind, value, formula, _ = cells.get(address, (None, None, None, None))
            new_kind, new_value, new_formula, font = new_cells.get(address, (None, None, None, None))
            entry = edits.get((sheet, address))
            if entry and entry["Target"] != entry["Source"]:
                expected = (kind, entry["Target"], None)
                matches = (kind == "text" and formula is None and value == entry["Source"]
                           and (new_kind, new_value, new_formula) == expected
                           and font in {FONT, "微软雅黑"})
            elif kind == "n" and new_kind == "n" and value is not None and new_value is not None:
                matches = formula == new_formula and math.isclose(float(value), float(new_value), rel_tol=1e-12, abs_tol=1e-9)
            else:
                matches = (kind, value, formula) == (new_kind, new_value, new_formula)
            if not matches:
                errors.append(f"Cell mismatch at {sheet}!{address}")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=["inventory", "refresh", "verify"])
    parser.add_argument("work_root", type=Path)
    parser.add_argument("--worker", type=Path, help="Built OleWorkbookWorker.dll")
    parser.add_argument("--timeout", type=int, default=180)
    args = parser.parse_args()
    folder = args.work_root.resolve() / "ole-v1"
    objects = read_json(folder / "batch_objects.json")
    keys = [item["Key"] for item in objects]
    if not keys or len(set(keys)) != len(keys) or any(not re.fullmatch(r"ole_\d+", key) for key in keys):
        raise ValueError("Expected a nonempty manifest with unique OLE object keys")
    plan_path = folder / "all_sheet_plan.json"
    if args.stage != "inventory":
        plan_list = read_json(plan_path)
        plans = {plan["File"]: plan for plan in plan_list}
        if len(plans) != len(plan_list) or set(plans) != set(keys):
            raise ValueError("Provide exactly one plan per exported OLE object")
    inventory, reports, storage_hashes = [], [], {}
    if args.stage == "refresh":
        write_json(folder / "refresh_receipt.json", {"Ready": False})
        plan_sha = digest(plan_path)
    if args.stage == "verify":
        write_json(folder / "ole_delivery_receipt.json", {"Ready": False})
        applied = read_json(folder / "batch_apply_report.json")
        completed = [key for drawing in applied for key in drawing["Done"]]
        if sorted(completed) != sorted(keys) or any(drawing["Errors"] for drawing in applied):
            raise ValueError("OLE apply report is incomplete or has errors")
        drawing_hashes = {}
        for drawing in applied:
            output = Path(drawing["Output"]).resolve()
            sha = digest(output)
            if sha != drawing["OutputSha256"]:
                raise ValueError(f"OLE drawing changed since its readback export: {output.name}")
            drawing_hashes[str(output)] = sha
    for key in keys:
        original = payload(folder / f"{key}.dxf")
        package, _ = workbook_parts(original[128:])
        book = Workbook(package)
        if args.stage == "inventory":
            for sheet in book.sheets:
                _, cells = book.snapshot(sheet)
                for address, (kind, value, formula, _) in cells.items():
                    if kind == "text" and formula is None:
                        inventory.append({"File": key, "Sheet": sheet, "Handle": address,
                                          "Source": value, "Displayed": sheet == book.active})
            continue
        edits = approved_entries(plans[key], book)
        if args.stage == "refresh":
            if args.worker is None or args.timeout <= 0:
                parser.error("refresh requires --worker and a positive --timeout")
            work = folder / "refresh-work" / key
            work.mkdir(parents=True, exist_ok=True)
            disposable = work / f"{key}.ole"
            disposable.write_bytes(original[128:])
            if edits:
                patched = work / "patched.xlsx"
                patch_package(book, edits, patched)
                with (work / "worker.log").open("wb") as log:
                    subprocess.run(["dotnet", str(args.worker.resolve()), str(disposable), "--package",
                                    str(patched), str(plan_path)], stdout=log, stderr=subprocess.STDOUT,
                                   check=True, timeout=args.timeout, creationflags=subprocess.CREATE_NO_WINDOW)
            refreshed, _ = workbook_parts(disposable.read_bytes())
            errors = verify_workbook(book, Workbook(refreshed), edits)
            if errors:
                raise ValueError(f"{key} refresh verification: {errors[:8]}")
            destination = folder / "refined" / f"{key}.ole"
            destination.parent.mkdir(exist_ok=True)
            temporary = destination.with_suffix(".tmp")
            shutil.copyfile(disposable, temporary)
            temporary.replace(destination)
            storage_hashes[key] = digest(destination)
        else:
            saved = payload(folder / f"native_verify_{key}.dxf")
            saved_package, presentation = workbook_parts(saved[128:])
            errors = verify_workbook(book, Workbook(saved_package), edits)
            if original[:124] != saved[:124]:
                errors.append("OLE placement header changed")
            if not presentation:
                errors.append("Missing presentation data")
            caches = folder / "translated"
            caches.mkdir(exist_ok=True)
            (caches / f"{key}_cache.bin").write_bytes(presentation)
            reports.append({"Key": key, "Sheets": len(book.sheets), "EditedCells": sum(
                e["Source"] != e["Target"] for e in edits.values()), "Errors": errors})
        print(f"{args.stage}: {key}", flush=True)
    if args.stage == "inventory":
        write_json(folder / "cell_inventory.json", inventory)
    if args.stage == "refresh":
        if digest(plan_path) != plan_sha:
            raise ValueError("The plan changed during refresh")
        write_json(folder / "refresh_receipt.json", {"Ready": True, "PlanSha256": plan_sha,
                                                     "StorageSha256": storage_hashes})
    if args.stage == "verify":
        write_json(folder / "verified_all_sheets.json", reports)
        if any(report["Errors"] for report in reports):
            raise SystemExit("Readback errors. See verified_all_sheets.json")
        write_json(folder / "ole_delivery_receipt.json", {"Ready": True, "DrawingSha256": drawing_hashes})


if __name__ == "__main__":
    main()
