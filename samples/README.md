# Sample files

Forty small, harmless files (PDFs, Word and Excel documents, text, Markdown, CSV, pictures, zips, a JSON, an HTML and an
SVG) to try AladdinRug's desktop sorter on without touching your own files. They are real, openable files, a few KB each,
and contain nothing but a title.

```powershell
.\samples\Add-SampleFiles.ps1
```

This copies them onto your Desktop (it never overwrites anything). Then right-click the rug and choose
**Merchant: sort the desktop files into folders**, and watch him carry them into "PDF files", "DOCX files", "CSV files"
and so on. The three one-off types (JSON, HTML, SVG) go into "Other files".

To clean up, undo the sort (**Undo: put the sorted files back**), then:

```powershell
.\samples\Add-SampleFiles.ps1 -Remove
```

That only deletes files on your Desktop that are identical to a sample, so a file of yours that happens to share a name is left alone.

The promo video in `docs/promo` was made from these files.
