#!/usr/bin/env python3
"""Draws a Ravencia size chart (beden tablosu) as a 1200x1800 JPEG from a JSON file.

Usage:
    python draw_size_chart.py --input chart.json [--output-dir DIR]

The JSON file:
    {
      "productName": "Oversize Pamuk Tişört",          # optional, shown under the title
      "unit": "cm",
      "columns": ["Boy", "En"],                          # measurement names, in order
      "rows": [{"size": "S", "values": ["64", "50"]}, ...]  # one row per size, values as text
    }

Every value is drawn exactly as given (text, never re-formatted or rounded). The script writes two files into the
output directory: beden-tablosu.jpg and beden-tablosu.json, the second holding exactly what was drawn so the
caller can compare it with what it asked for. It exits with a non-zero status and a message when the input is not
usable; then no image is written.
"""

import argparse
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont

WIDTH, HEIGHT = 1200, 1800
MARGIN = 80

# Ravencia light theme tokens (src/MarketplaceHub.Web/src/styles/tokens.css, :root[data-theme="light"]).
BACKGROUND = (255, 255, 255)
INK = (0x2F, 0x3A, 0x4F)
MUTED = (0x52, 0x5E, 0x77)
BORDER = (0xC9, 0xD0, 0xDC)
PRIMARY = (0x5B, 0x44, 0xD2)
PRIMARY_SOFT = (0xE0, 0xDC, 0xF6)
ZEBRA = (0xF0, 0xF2, 0xF6)
WHITE = (255, 255, 255)

HERE = os.path.dirname(os.path.abspath(__file__))
LOGO = os.path.join(HERE, "..", "assets", "ravencia-symbol.png")


class ChartError(Exception):
    pass


def font_candidates(bold):
    names = ["DejaVuSans-Bold.ttf"] if bold else ["DejaVuSans.ttf"]
    folders = [os.path.join(HERE, "..", "assets", "fonts")]
    try:
        import matplotlib

        folders.append(os.path.join(matplotlib.get_data_path(), "fonts", "ttf"))
    except Exception:  # matplotlib is optional
        pass
    folders += ["/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/dejavu", "/usr/share/fonts/TTF"]
    for folder in folders:
        for name in names:
            yield os.path.join(folder, name)


def load_font(size, bold=False):
    # DejaVu covers the Turkish letters (ğ, ş, ı, İ, ç, ö, ü); Pillow's default font does not.
    for path in font_candidates(bold):
        if os.path.isfile(path):
            return ImageFont.truetype(path, size)
    raise ChartError("Türkçe karakterleri çizebilen DejaVu yazı tipi bulunamadı.")


def text_width(draw, text, font):
    left, _, right, _ = draw.textbbox((0, 0), text, font=font)
    return right - left


def fit_font(draw, texts, max_width, start, minimum, bold=False):
    size = start
    while size > minimum:
        font = load_font(size, bold)
        if all(text_width(draw, text, font) <= max_width for text in texts):
            return font
        size -= 2
    return load_font(minimum, bold)


def centered(draw, box, text, font, fill):
    left, top, right, bottom = box
    bbox = draw.textbbox((0, 0), text, font=font)
    x = left + (right - left - (bbox[2] - bbox[0])) / 2 - bbox[0]
    y = top + (bottom - top - (bbox[3] - bbox[1])) / 2 - bbox[1]
    draw.text((x, y), text, font=font, fill=fill)


def read_input(path):
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    columns = data.get("columns")
    rows = data.get("rows")
    if not isinstance(columns, list) or not 1 <= len(columns) <= 8 or not all(isinstance(c, str) and c.strip() for c in columns):
        raise ChartError("columns: 1-8 kolon adı gerekli.")
    if not isinstance(rows, list) or not 1 <= len(rows) <= 30:
        raise ChartError("rows: 1-30 beden satırı gerekli.")
    clean_rows = []
    for row in rows:
        size = row.get("size") if isinstance(row, dict) else None
        values = row.get("values") if isinstance(row, dict) else None
        if not isinstance(size, str) or not size.strip():
            raise ChartError("Her satırda beden adı (size) gerekli.")
        if not isinstance(values, list) or len(values) != len(columns) or not all(isinstance(v, str) and v.strip() for v in values):
            raise ChartError(f"'{size}' satırında her kolon için bir değer (metin) gerekli.")
        clean_rows.append({"size": size, "values": list(values)})
    unit = data.get("unit") or "cm"
    product = data.get("productName") or ""
    return {"productName": product, "unit": unit, "columns": list(columns), "rows": clean_rows}


def draw_chart(chart):
    image = Image.new("RGB", (WIDTH, HEIGHT), BACKGROUND)
    draw = ImageDraw.Draw(image)

    # Header: logo, title, product name.
    y = MARGIN
    if os.path.isfile(LOGO):
        logo = Image.open(LOGO).convert("RGBA")
        logo.thumbnail((120, 120))
        image.paste(logo, ((WIDTH - logo.width) // 2, y), logo)
        y += logo.height + 28
    title_font = load_font(76, bold=True)
    centered(draw, (0, y, WIDTH, y + 90), "BEDEN TABLOSU", title_font, INK)
    y += 100
    if chart["productName"]:
        product_font = fit_font(draw, [chart["productName"]], WIDTH - 2 * MARGIN, 38, 22)
        centered(draw, (0, y, WIDTH, y + 50), chart["productName"], product_font, MUTED)
        y += 60
    unit_font = load_font(30)
    centered(draw, (0, y, WIDTH, y + 40), f"Tüm ölçüler {chart['unit']} cinsindendir.", unit_font, MUTED)
    y += 70

    # Table: Beden column + one column per measurement.
    columns = ["Beden"] + [f"{name} ({chart['unit']})" for name in chart["columns"]]
    rows = chart["rows"]
    table_left, table_right = MARGIN, WIDTH - MARGIN
    table_width = table_right - table_left
    footer_height = 150
    available = HEIGHT - y - footer_height
    header_height = 110
    row_height = max(56, min(110, (available - header_height) // max(1, len(rows))))
    if header_height + row_height * len(rows) > available:
        raise ChartError("Bu kadar beden satırı tek görsele sığmıyor.")
    column_width = table_width / len(columns)

    header_font = fit_font(draw, columns, column_width - 24, 34, 16, bold=True)
    cell_texts = [row["size"] for row in rows] + [value for row in rows for value in row["values"]]
    cell_font = fit_font(draw, cell_texts, column_width - 24, min(44, int(row_height * 0.48)), 16)
    size_font = fit_font(draw, [row["size"] for row in rows], column_width - 24, min(44, int(row_height * 0.48)), 16, bold=True)

    table_height = header_height + row_height * len(rows)
    draw.rounded_rectangle((table_left, y, table_right, y + table_height), radius=14, fill=WHITE, outline=BORDER, width=2)
    draw.rounded_rectangle((table_left, y, table_right, y + header_height), radius=14, fill=PRIMARY)
    draw.rectangle((table_left, y + header_height - 14, table_right, y + header_height), fill=PRIMARY)
    for index, name in enumerate(columns):
        left = table_left + index * column_width
        centered(draw, (left, y, left + column_width, y + header_height), name, header_font, WHITE)

    drawn_rows = []
    row_top = y + header_height
    for index, row in enumerate(rows):
        if index % 2 == 1:
            bottom = row_top + row_height
            if index == len(rows) - 1:
                draw.rounded_rectangle((table_left + 1, row_top, table_right - 1, bottom - 1), radius=12, fill=ZEBRA)
                draw.rectangle((table_left + 1, row_top, table_right - 1, bottom - 14), fill=ZEBRA)
            else:
                draw.rectangle((table_left + 1, row_top, table_right - 1, bottom), fill=ZEBRA)
        size_left = table_left
        draw.rectangle((size_left + 1, row_top, size_left + column_width, row_top + row_height - (1 if index == len(rows) - 1 else 0)), fill=PRIMARY_SOFT if index % 2 == 0 else (0xD6, 0xD1, 0xF2))
        centered(draw, (size_left, row_top, size_left + column_width, row_top + row_height), row["size"], size_font, PRIMARY)
        for column_index, value in enumerate(row["values"]):
            left = table_left + (column_index + 1) * column_width
            centered(draw, (left, row_top, left + column_width, row_top + row_height), value, cell_font, INK)
        if index < len(rows) - 1:
            draw.line((table_left, row_top + row_height, table_right, row_top + row_height), fill=BORDER, width=1)
        drawn_rows.append({"size": row["size"], "values": list(row["values"])})
        row_top += row_height
    for index in range(1, len(columns)):
        x = table_left + index * column_width
        draw.line((x, y + header_height, x, y + table_height), fill=BORDER, width=1)
    draw.rounded_rectangle((table_left, y, table_right, y + table_height), radius=14, outline=BORDER, width=2)

    # Footer.
    note = "Ölçüler ürün düz zeminde serilerek alınmıştır; ±1-2 cm fark olabilir."
    note_font = fit_font(draw, [note], WIDTH - 2 * MARGIN, 26, 16)
    centered(draw, (0, HEIGHT - footer_height + 30, WIDTH, HEIGHT - footer_height + 70), note, note_font, MUTED)
    brand_font = load_font(30, bold=True)
    centered(draw, (0, HEIGHT - 80, WIDTH, HEIGHT - 40), "RAVENCIA", brand_font, PRIMARY)

    drawn = {"unit": chart["unit"], "columns": list(chart["columns"]), "rows": drawn_rows}
    return image, drawn


def output_directory(explicit):
    if explicit:
        return explicit
    for candidate in (os.environ.get("OUTPUT_DIR"), "/mnt/user-data/outputs"):
        if candidate and os.path.isdir(candidate):
            return candidate
    return os.getcwd()


def main():
    parser = argparse.ArgumentParser(description="Ravencia beden tablosu çizer.")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output-dir")
    arguments = parser.parse_args()
    try:
        chart = read_input(arguments.input)
        image, drawn = draw_chart(chart)
    except (ChartError, ValueError, OSError) as error:
        print(f"HATA: {error}", file=sys.stderr)
        return 2
    directory = output_directory(arguments.output_dir)
    os.makedirs(directory, exist_ok=True)
    image_path = os.path.join(directory, "beden-tablosu.jpg")
    echo_path = os.path.join(directory, "beden-tablosu.json")
    image.save(image_path, "JPEG", quality=92, optimize=True)
    with open(echo_path, "w", encoding="utf-8") as handle:
        json.dump(drawn, handle, ensure_ascii=False)
    print(f"OK {image_path} {echo_path} {WIDTH}x{HEIGHT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
