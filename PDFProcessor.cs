using iTextSharp.text.pdf;
using iTextSharp.text.pdf.parser;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// PDF Processor Service
    /// 
    /// Processes PDF files and extracts content for AI analysis.
    /// Supports both text-based and image-based PDFs.
    /// 
    /// Features:
    /// - Text extraction from text-based PDFs
    /// - Image extraction from embedded JPEG images
    /// - Automatic detection of PDF type (text vs. image)
    /// - Base64 encoding of images for Azure OpenAI Vision API
    /// 
    /// Architecture:
    /// - Uses iTextSharp for PDF reading and text extraction
    /// - Extracts JPEG images directly (no conversion needed)
    /// - Returns structured PDFContent object
    /// 
    /// Limitations:
    /// - Currently supports JPEG extraction only
    /// - For complex image formats, consider using additional libraries
    /// 
    /// Usage:
    /// var processor = new PDFProcessor();
    /// var content = processor.ProcessPDF("document.pdf");
    /// if (content.IsTextBased) { use text } else { use images }
    /// </summary>
    public class PDFProcessor
    {
        #region Constants and Configuration

        // Minimum text length to consider PDF as text-based
        private const int MIN_TEXT_LENGTH = 100;

        // Maximum number of images to extract (to prevent memory issues)
        private const int MAX_IMAGES_PER_PDF = 50;

        // Minimum image dimensions to avoid extracting tiny icons/logos
        private const int MIN_IMAGE_WIDTH = 100;
        private const int MIN_IMAGE_HEIGHT = 100;

        // Regex pattern to detect meaningful text (10+ consecutive letters)
        private static readonly Regex MEANINGFUL_TEXT_PATTERN = new Regex(@"[A-Za-zÄÖÜäöüß]{10,}");

        #endregion

        #region Data Classes

        /// <summary>
        /// Container for processed PDF content.
        /// Includes both text and images in a format suitable for AI analysis.
        /// </summary>
        public class PDFContent
        {
            /// <summary>
            /// Indicates whether the PDF is primarily text-based.
            /// True: Use text extraction for analysis.
            /// False: Use images for vision-based analysis.
            /// </summary>
            public bool IsTextBased { get; private set; }

            /// <summary>
            /// Extracted text content from the PDF.
            /// Empty string if no text could be extracted.
            /// </summary>
            public string TextContent { get; private set; }

            /// <summary>
            /// List of base64-encoded images extracted from the PDF.
            /// Format: data:image/jpeg;base64,... or data:image/png;base64,...
            /// Ready for Azure OpenAI Vision API.
            /// </summary>
            public List<string> Base64Images { get; private set; }

            /// <summary>
            /// Number of pages in the PDF.
            /// </summary>
            public int PageCount { get; private set; }

            public PDFContent(bool textBased, string textContent, List<string> base64Images, int pageCount)
            {
                IsTextBased = textBased;
                TextContent = textContent ?? "";
                Base64Images = base64Images ?? new List<string>();
                PageCount = pageCount;
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Processes a PDF file and extracts text and/or images.
        /// Automatically detects whether the PDF is text-based or image-based.
        /// </summary>
        /// <param name="pdfFilePath">Full path to the PDF file</param>
        /// <returns>PDFContent object containing extracted text and/or images</returns>
        /// <exception cref="IOException">Thrown if PDF cannot be read or processed</exception>
        public PDFContent ProcessPDF(string pdfFilePath)
        {
            try
            {
                Console.WriteLine($"\n=== PDF-VERARBEITUNG MIT iTEXTSHARP ===");
                Console.WriteLine($"Verarbeite PDF: {pdfFilePath}");

                using (var reader = new PdfReader(pdfFilePath))
                {
                    int pageCount = reader.NumberOfPages;

                    // Extract text from all pages
                    string extractedText = ExtractTextFromAllPages(reader);

                    // Determine if PDF is text-based or image-based
                    bool isTextBased = IsTextBasedPDF(extractedText);

                    Console.WriteLine($"PDF-Analyse:");
                    Console.WriteLine($"  - Seiten: {pageCount}");
                    Console.WriteLine($"  - Extrahierte Zeichen: {extractedText.Length}");
                    Console.WriteLine($"  - Typ: {(isTextBased ? "Text-basiert" : "Bild-basiert")}");

                    // Extract embedded images
                    List<string> embeddedImages = ExtractEmbeddedImages(reader);
                    Console.WriteLine($"  - Eingebettete Bilder: {embeddedImages.Count}");

                    if (isTextBased)
                    {
                        Console.WriteLine("Text-basierte PDF erkannt - verwende Textextraktion");
                        
                        // Show text preview
                        if (extractedText.Length > 0)
                        {
                            int previewLength = Math.Min(200, extractedText.Length);
                            Console.WriteLine($"  Textvorschau: {extractedText.Substring(0, previewLength)}...");
                        }
                        
                        return new PDFContent(true, extractedText, embeddedImages, pageCount);
                    }
                    else
                    {
                        Console.WriteLine("Bild-basierte PDF erkannt - verwende Bildextraktion");
                        
                        // For image-based PDFs, ensure we have images
                        if (embeddedImages.Count == 0)
                        {
                            Console.WriteLine("WARNUNG: Keine Bilder extrahiert - PDF könnte leer sein oder geschützt");
                            Console.WriteLine("  Mögliche Ursachen:");
                            Console.WriteLine("  1. PDF ist geschützt (DRM)");
                            Console.WriteLine("  2. PDF enthält gerenderte Seiten statt eingebettete Bilder");
                            Console.WriteLine("  3. Bildformat wird nicht unterstützt");
                        }
                        
                        return new PDFContent(false, extractedText, embeddedImages, pageCount);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fehler beim Verarbeiten der PDF: {e.Message}");
                Console.WriteLine(e.StackTrace);
                throw new IOException($"Fehler beim Verarbeiten der PDF: {e.Message}", e);
            }
        }

        #endregion

        #region Text Extraction

        /// <summary>
        /// Extracts text from all pages of the PDF.
        /// Handles page-level errors gracefully.
        /// </summary>
        private string ExtractTextFromAllPages(PdfReader reader)
        {
            var textParts = new List<string>();

            for (int page = 1; page <= reader.NumberOfPages; page++)
            {
                try
                {
                    string pageText = PdfTextExtractor.GetTextFromPage(reader, page);
                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        textParts.Add(pageText);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warnung: Fehler beim Extrahieren von Text auf Seite {page}: {ex.Message}");
                }
            }

            return string.Join("\n", textParts).Trim();
        }

        /// <summary>
        /// Determines if a PDF is text-based by analyzing extracted text.
        /// Checks both text length and presence of meaningful words.
        /// </summary>
        private bool IsTextBasedPDF(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (text.Length < MIN_TEXT_LENGTH)
                return false;

            return MEANINGFUL_TEXT_PATTERN.IsMatch(text);
        }

        /// <summary>
        /// Checks if text contains meaningful words (not just numbers/symbols).
        /// </summary>
        private bool ContainsMeaningfulText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return MEANINGFUL_TEXT_PATTERN.IsMatch(text);
        }

        #endregion

        #region Image Extraction

        /// <summary>
        /// Extracts all embedded images from the PDF.
        /// Currently supports JPEG images (most common in technical documents).
        /// Encodes images as base64 data URLs for Azure OpenAI Vision API.
        /// Filters out small images (likely icons/logos).
        /// </summary>
        private List<string> ExtractEmbeddedImages(PdfReader reader)
        {
            var base64Images = new List<string>();
            int extractedCount = 0;
            int skippedCount = 0;

            try
            {
                for (int pageNum = 1; pageNum <= reader.NumberOfPages && extractedCount < MAX_IMAGES_PER_PDF; pageNum++)
                {
                    PdfDictionary page = reader.GetPageN(pageNum);
                    PdfDictionary resources = page.GetAsDict(PdfName.RESOURCES);

                    if (resources != null)
                    {
                        PdfDictionary xobjects = resources.GetAsDict(PdfName.XOBJECT);
                        
                        if (xobjects != null)
                        {
                            foreach (PdfName name in xobjects.Keys)
                            {
                                if (extractedCount >= MAX_IMAGES_PER_PDF)
                                {
                                    Console.WriteLine($"  HINWEIS: Maximum von {MAX_IMAGES_PER_PDF} Bildern erreicht");
                                    break;
                                }

                                PdfObject obj = xobjects.Get(name);
                                
                                if (obj.IsIndirect())
                                {
                                    PdfDictionary imageDict = (PdfDictionary)PdfReader.GetPdfObject(obj);
                                    PdfName subtype = (PdfName)PdfReader.GetPdfObject(imageDict.Get(PdfName.SUBTYPE));

                                    if (PdfName.IMAGE.Equals(subtype))
                                    {
                                        try
                                        {
                                            string base64Image = ExtractImageFromXObject(imageDict, pageNum);
                                            
                                            if (!string.IsNullOrEmpty(base64Image))
                                            {
                                                base64Images.Add(base64Image);
                                                extractedCount++;
                                                Console.WriteLine($"    Bild {extractedCount} extrahiert von Seite {pageNum}");
                                            }
                                            else
                                            {
                                                skippedCount++;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"    Fehler beim Extrahieren von Bild auf Seite {pageNum}: {ex.Message}");
                                            skippedCount++;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (skippedCount > 0)
                {
                    Console.WriteLine($"  {skippedCount} Bilder übersprungen (zu klein, nicht unterstützt, oder Fehler)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler bei Bild-Extraktion: {ex.Message}");
            }

            return base64Images;
        }

        /// <summary>
        /// Extracts an image from a PDF XObject and converts it to base64.
        /// Currently supports JPEG images (DCTDecode filter).
        /// Filters out small images based on dimensions.
        /// </summary>
        private string ExtractImageFromXObject(PdfDictionary imageDict, int pageNum)
        {
            try
            {
                // Get image dimensions
                PdfNumber width = imageDict.GetAsNumber(PdfName.WIDTH);
                PdfNumber height = imageDict.GetAsNumber(PdfName.HEIGHT);

                if (width != null && height != null)
                {
                    int imgWidth = width.IntValue;
                    int imgHeight = height.IntValue;

                    // Skip small images (likely icons/logos)
                    if (imgWidth < MIN_IMAGE_WIDTH || imgHeight < MIN_IMAGE_HEIGHT)
                    {
                        Console.WriteLine($"    Überspringe kleines Bild ({imgWidth}x{imgHeight}) auf Seite {pageNum}");
                        return null;
                    }
                }

                // Get image filter (compression type)
                PdfObject filterObj = PdfReader.GetPdfObject(imageDict.Get(PdfName.FILTER));
                string filterName = GetFilterName(filterObj);

                // Extract based on filter type
                if (filterName == "/DCTDecode")
                {
                    // JPEG images can be extracted directly (no conversion needed)
                    return ExtractJpegImage(imageDict);
                }
                else if (filterName == "/FlateDecode")
                {
                    // PNG/TIFF images (requires System.Drawing for conversion)
                    Console.WriteLine($"    FlateDecode-Bilder werden übersprungen (System.Drawing nicht verfügbar)");
                    return null;
                }
                else if (filterName == "/JPXDecode")
                {
                    // JPEG2000 images
                    Console.WriteLine($"    JPEG2000-Bilder werden übersprungen (spezielle Bibliothek erforderlich)");
                    return null;
                }
                else
                {
                    Console.WriteLine($"    Nicht unterstütztes Bildformat: {filterName ?? "unbekannt"}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Bild-Extraktion fehlgeschlagen: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the filter name from a PdfObject (can be single filter or array).
        /// </summary>
        private string GetFilterName(PdfObject filterObj)
        {
            if (filterObj == null)
                return null;

            if (filterObj is PdfName)
            {
                return ((PdfName)filterObj).ToString();
            }
            else if (filterObj is PdfArray)
            {
                PdfArray filterArray = (PdfArray)filterObj;
                if (filterArray.Size > 0)
                {
                    return ((PdfName)filterArray[0]).ToString();
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts JPEG image directly as base64 data URL.
        /// JPEG images are already compressed and can be used without conversion.
        /// Format: data:image/jpeg;base64,/9j/4AAQSkZJRg...
        /// </summary>
        private string ExtractJpegImage(PdfDictionary imageDict)
        {
            try
            {
                // Get raw JPEG bytes
                byte[] imageBytes = PdfReader.GetStreamBytesRaw((PRStream)imageDict);
                
                // Convert to base64
                string base64 = Convert.ToBase64String(imageBytes);
                
                // Return as data URL (ready for Azure OpenAI Vision API)
                return $"data:image/jpeg;base64,{base64}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    JPEG-Extraktion fehlgeschlagen: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
