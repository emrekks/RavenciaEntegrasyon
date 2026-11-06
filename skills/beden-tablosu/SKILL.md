---
name: beden-tablosu
description: Draws a Ravencia-branded size chart (beden tablosu) image, 1200x1800 portrait JPEG, from given sizes and measurements in cm. Use when asked to create a size chart / beden tablosu for a product.
---

# Beden Tablosu (Ravencia)

Draws a clean size chart for a product listing: Ravencia logo, Turkish title "BEDEN TABLOSU", the product name,
a table with one row per size (Beden) and one column per measurement in cm, and a short measuring note. The design
(colours, fonts, layout) is fixed in `scripts/draw_size_chart.py`; do not draw the chart any other way.

## Rules

- The numbers are the seller's measurements. Copy every size and every value exactly as given, as text, in the
  given order. Never invent, round, convert, reorder, translate or "fix" a value, a size or a column name.
- If the input is incomplete or unclear, do not guess: stop and say what is missing.
- Produce exactly one image. Do not add sizes, columns, notes or decorations.

## Steps

1. Write the input as JSON to `chart.json` (UTF-8):

   ```json
   {
     "productName": "Oversize Pamuk Tişört",
     "unit": "cm",
     "columns": ["Boy", "En"],
     "rows": [
       {"size": "S", "values": ["64", "50"]},
       {"size": "M", "values": ["66", "53"]}
     ]
   }
   ```

   `values` holds one text value per column, in the column order.

2. Run the script from this skill's folder:

   ```bash
   python scripts/draw_size_chart.py --input chart.json --output-dir /mnt/user-data/outputs
   ```

   It writes `beden-tablosu.jpg` (1200x1800 JPEG) and `beden-tablosu.json` (exactly what it drew) into the output
   directory and prints `OK ...`. On a problem it prints `HATA: ...` and writes nothing.

3. Reply with one short line: `OK` when the script printed `OK`, otherwise the error it printed.
