"""Group candidate omissions and terminology conflicts for a human review pass."""

import argparse
from collections import defaultdict
import json
from pathlib import Path, PureWindowsPath
import re


CONTROL = re.compile(r"\\[fFhHwWcCtTqQap][^;\\{}]*;|\\[PXx~LlOoKk]|[{}]")
FOREIGN = re.compile(r"[A-Za-z]{2,}|[\u0600-\u06ff\u0400-\u04ff]")
CHINESE = re.compile(r"[\u3400-\u9fff]")
NUMBER = re.compile(r"\d+(?:[.,]\d+)*")


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def plain(value):
    return re.sub(r"\s+", " ", CONTROL.sub(lambda m: " " if m[0] == r"\P" else "", value)).strip()


def key(row):
    if row["Kind"] != "Table":
        return row["Handle"], None, None
    if row.get("Row") is not None and row.get("Column") is not None:
        return row["Handle"], row["Row"], row["Column"]
    position = re.search(r"/cell:(\d+),(\d+)$", row.get("Location", ""))
    if not position:
        raise ValueError(f"Missing cell coordinates for {row['Handle']}")
    return row["Handle"], int(position[1]), int(position[2])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_scan", type=Path)
    proposed = parser.add_mutually_exclusive_group(required=True)
    proposed.add_argument("--plan", type=Path)
    proposed.add_argument("--saved-scan", type=Path)
    parser.add_argument("--drawing-map", type=Path, help="Optional source-relative to saved-relative name map")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sources = load(args.source_scan)
    targets = {doc["File"]: doc for doc in load(args.plan or args.saved_scan)}
    mapping = load(args.drawing_map) if args.drawing_map else {}
    groups = defaultdict(list)
    variants = defaultdict(set)
    checked = 0
    for source in sources:
        name = source["File"]
        saved = PureWindowsPath(name)
        other = mapping.get(name, str(saved.with_name(saved.stem + "_ZH.dwg"))) if args.saved_scan else name
        document = targets[other]
        if source.get("Error") or document.get("Error") or source.get("Issues") or document.get("Issues"):
            raise ValueError(f"Incomplete scan for {name}")
        index = {key(row): row for row in document["Rows" if args.saved_scan else "Entries"]}
        for row in source["Rows"]:
            if not row["Reachable"] or not row["Visible"] or "%<" in row["Raw"]:
                continue
            checked += 1
            source_text = plain(row["Raw"])
            match = index.get(key(row))
            if args.saved_scan and match is None:
                groups[("missing-address", source_text, "")].append({"File": name, "Handle": row["Handle"], "Location": row.get("Location")})
                continue
            target_text = plain(match["Raw" if args.saved_scan else "Target"] if match else row["Raw"])
            variants[source_text].add(target_text)
            flags = []
            if FOREIGN.search(target_text):
                flags.append("mixed-or-retained" if CHINESE.search(target_text) else "untranslated-or-identifier")
            if NUMBER.findall(source_text) != NUMBER.findall(target_text):
                flags.append("numeric-token-difference")
            location = {"File": name, "Handle": row["Handle"], "Location": row.get("Location"),
                        "Layer": row.get("Layer"), "X": row.get("X"), "Y": row.get("Y")}
            for flag in flags:
                groups[(flag, source_text, target_text)].append(location)
    report = {
        "RowsChecked": checked,
        "Candidates": [{"Flag": flag, "Source": source, "Target": target, "Locations": locations}
                       for (flag, source, target), locations in groups.items()],
        "SourceVariants": [{"Source": source, "Targets": sorted(values)}
                           for source, values in variants.items() if len(values) > 1],
    }
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Reviewed inventory rows: {checked}; candidate groups: {len(groups)}")


if __name__ == "__main__":
    main()
