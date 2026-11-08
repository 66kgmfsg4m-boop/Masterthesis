using System;
using System.IO;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// PDF File Manager Service
    /// 
    /// Manages automatic archiving of PDF files to a configured directory.
    /// Supports both Azure-configured paths and local fallback paths.
    /// 
    /// Purpose:
    /// - Automatically copy processed PDFs to archive directory
    /// - Support network paths and local paths
    /// - Load archive path from Azure configuration
    /// - Create directories if they don't exist
    /// - Verify write permissions
    /// 
    /// Configuration:
    /// - Primary: Azure Blob Storage (authorized-users config)
    /// - Fallback: Local path (C:\Temp\PDFArchiv\)
    /// </summary>
    public class PDFFileManagerService
    {
        // Default local path (used if Azure configuration is unavailable)
        private const string DEFAULT_TARGET_DIRECTORY = @"C:\Temp\PDFArchiv\";
        
        // Azure configuration key for archive path
        private const string CONFIG_KEY_PDF_ARCHIVE_PATH = "pdf.archive.path";

        private readonly string _targetDirectory;
        private readonly RemoteConfigService _remoteConfigService;

        /// <summary>
        /// Initializes the PDF file manager service.
        /// Attempts to load archive path from Azure configuration.
        /// Falls back to local path if Azure is unavailable.
        /// Creates target directory if it doesn't exist.
        /// </summary>
        public PDFFileManagerService()
        {
            _remoteConfigService = new RemoteConfigService(Guid.NewGuid().ToString());

            // Try to load path from Azure configuration
            string configuredPath = LoadArchivePathFromConfig();

            if (string.IsNullOrEmpty(configuredPath))
            {
                // Fallback to local path
                _targetDirectory = DEFAULT_TARGET_DIRECTORY;
                Console.WriteLine($"Keine Azure-Konfiguration gefunden - verwende lokalen Pfad: {_targetDirectory}");

                // Create directory if it doesn't exist
                try
                {
                    if (!Directory.Exists(_targetDirectory))
                    {
                        Directory.CreateDirectory(_targetDirectory);
                        Console.WriteLine($"Verzeichnis erstellt: {_targetDirectory}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Fehler beim Erstellen des Verzeichnisses: {e.Message}");
                }
            }
            else
            {
                // Use Azure-configured path
                _targetDirectory = NormalizePathEncoding(configuredPath);
                Console.WriteLine($"PDF-Archiv-Pfad aus Azure-Konfiguration geladen: {_targetDirectory}");
            }

            // Verify directory exists and has write permissions
            CheckTargetDirectoryExists();
        }

        /// <summary>
        /// Loads archive path from Azure Blob Storage configuration.
        /// Searches for path in "authorized-users" config file.
        /// </summary>
        /// <returns>Configured path or null if not found</returns>
        private string LoadArchivePathFromConfig()
        {
            try
            {
                // Load configuration from Azure
                var config = _remoteConfigService.LoadConfig("authorized-users");
                
                if (config != null && config.ContainsKey(CONFIG_KEY_PDF_ARCHIVE_PATH))
                {
                    string path = config[CONFIG_KEY_PDF_ARCHIVE_PATH];
                    Console.WriteLine($"Azure-Pfad gefunden: {path}");
                    return path;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Konnte PDF-Archiv-Pfad nicht aus Azure laden: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Normalizes path encoding by removing double backslashes and ensuring trailing backslash.
        /// Handles both UNC paths and local paths.
        /// </summary>
        /// <param name="path">Raw path from configuration</param>
        /// <returns>Normalized path</returns>
        private string NormalizePathEncoding(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Remove double backslashes (except for UNC paths)
            // Add trailing backslash if missing
            return path.Replace("\\\\", "\\").TrimEnd('\\') + "\\";
        }

        /// <summary>
        /// Copies a PDF file to the configured target directory.
        /// Overwrites existing files if they already exist.
        /// Creates target directory if it doesn't exist.
        /// </summary>
        /// <param name="sourceFilePath">Full path to source PDF file</param>
        /// <returns>True if copy was successful, false otherwise</returns>
        public bool CopyPDFToTargetDirectory(string sourceFilePath)
        {
            try
            {
                // Validate source file exists
                if (!File.Exists(sourceFilePath))
                {
                    Console.WriteLine($"Quelldatei existiert nicht: {sourceFilePath}");
                    return false;
                }

                // Create target directory if it doesn't exist
                if (!Directory.Exists(_targetDirectory))
                {
                    Console.WriteLine($"Erstelle Zielverzeichnis: {_targetDirectory}");
                    Directory.CreateDirectory(_targetDirectory);
                }

                // Construct target path
                string fileName = Path.GetFileName(sourceFilePath);
                string targetPath = Path.Combine(_targetDirectory, fileName);

                Console.WriteLine($"=== PDF-KOPIERVORGANG ===");
                Console.WriteLine($"Quelle: {sourceFilePath}");
                Console.WriteLine($"Ziel: {targetPath}");

                // Check if file already exists
                if (File.Exists(targetPath))
                {
                    Console.WriteLine($"Datei existiert bereits - wird überschrieben: {fileName}");
                }

                // Copy file and overwrite if exists
                File.Copy(sourceFilePath, targetPath, overwrite: true);

                Console.WriteLine($"PDF erfolgreich kopiert: {fileName}");
                return true;
            }
            catch (IOException e)
            {
                // Handle IO-specific errors with helpful messages
                Console.WriteLine($"Fehler beim Kopieren der PDF-Datei: {e.Message}");
                Console.WriteLine($"  Quelldatei: {sourceFilePath}");
                Console.WriteLine($"  Zielverzeichnis: {_targetDirectory}");

                // Provide specific error hints based on exception message
                if (e.Message.Contains("Access is denied") || e.Message.Contains("Zugriff verweigert"))
                {
                    Console.WriteLine("  Mögliche Ursache: Keine Berechtigung für das Zielverzeichnis");
                }
                else if (e.Message.Contains("network path") || e.Message.Contains("Netzwerkpfad"))
                {
                    Console.WriteLine("  Mögliche Ursache: Netzwerkpfad nicht erreichbar");
                }
                else if (e.Message.Contains("cannot find the path") || e.Message.Contains("Pfad nicht gefunden"))
                {
                    Console.WriteLine("  Mögliche Ursache: Zielverzeichnis existiert nicht");
                }

                return false;
            }
            catch (Exception e)
            {
                // Handle unexpected errors
                Console.WriteLine($"Unerwarteter Fehler beim Kopieren der PDF: {e.Message}");
                Console.WriteLine(e.StackTrace);
                return false;
            }
        }

        /// <summary>
        /// Checks if target directory exists and is writable.
        /// Creates directory if it doesn't exist.
        /// Tests write permissions by creating a temporary file.
        /// </summary>
        private void CheckTargetDirectoryExists()
        {
            try
            {
                Console.WriteLine("=== VERZEICHNIS-ÜBERPRÜFUNG ===");
                Console.WriteLine($"Überprüfe Pfad: {_targetDirectory}");

                if (Directory.Exists(_targetDirectory))
                {
                    Console.WriteLine($"Zielverzeichnis existiert: {_targetDirectory}");

                    // Test write permissions by creating and deleting a temporary file
                    try
                    {
                        string testFile = Path.Combine(_targetDirectory, $"test_{Guid.NewGuid()}.tmp");
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                        Console.WriteLine("Schreibberechtigung OK");
                    }
                    catch
                    {
                        Console.WriteLine($"Warnung: Keine Schreibberechtigung für Verzeichnis: {_targetDirectory}");
                    }
                }
                else
                {
                    Console.WriteLine($"Zielverzeichnis existiert nicht: {_targetDirectory}");
                    Console.WriteLine("  Versuche Verzeichnis zu erstellen...");

                    try
                    {
                        Directory.CreateDirectory(_targetDirectory);
                        Console.WriteLine($"Verzeichnis erstellt: {_targetDirectory}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Fehler: Konnte Verzeichnis nicht erstellen: {e.Message}");
                        Console.WriteLine("Bitte erstellen Sie das Verzeichnis manuell oder konfigurieren Sie einen anderen Pfad.");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fehler beim Überprüfen des Zielverzeichnisses: {e.Message}");
                Console.WriteLine($"Verzeichnis: {_targetDirectory}");
            }
        }

        /// <summary>
        /// Checks if target directory is accessible.
        /// </summary>
        /// <returns>True if directory exists, false otherwise</returns>
        public bool IsTargetDirectoryAccessible()
        {
            try
            {
                return Directory.Exists(_targetDirectory);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the configured target directory path.
        /// </summary>
        /// <returns>Full path to target directory</returns>
        public string GetTargetDirectory()
        {
            return _targetDirectory;
        }
    }
}
