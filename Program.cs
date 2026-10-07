using Azure.Storage.Blobs;
using System;
using System.IO;

namespace AzureBlobUploader
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("???????????????????????????????????????????????????????????");
            Console.WriteLine(" Azure Blob Multi-Upload Tool");
            Console.WriteLine("???????????????????????????????????????????????????????????");
            Console.WriteLine();

            // Configuration - aus Umgebungsvariablen (sicherer)
            string connectionString = Environment.GetEnvironmentVariable("AzureStorage__ConnectionString")
                ?? throw new InvalidOperationException("AzureStorage__ConnectionString environment variable is not set.");
            string containerName = Environment.GetEnvironmentVariable("AzureStorage__ContainerName") ?? "data";
            
            // Upload ALLE relevanten Prompts (Memory, InterfaceIC, Coil, EMI, Identification, Pitch, ...)
            var filesToUpload = new[]
            {
                // Stage 1: Identifikation
                ("IdentificationPrompt.txt",                       "..\\Azure_Prompts\\IdentificationPrompt.txt"),
                // Memory (Parallel + Serial)
                ("PartNumberDecodingPrompt_Memory.txt",            "..\\Azure_Prompts\\PartNumberDecodingPrompt_Memory.txt"),
                ("SeriesDatasheetInstructions_Memory.txt",         "..\\Azure_Prompts\\SeriesDatasheetInstructions_Memory.txt"),
                ("memory_extraction_guidelines_v3.txt",            "..\\Azure_Prompts\\memory_extraction_guidelines_v3.txt"),
                ("property_definitions_memory.txt",                "..\\Azure_Prompts\\property_definitions_memory.txt"),
                // Interface IC
                ("SeriesDatasheetInstructions_InterfaceIC.txt",    "..\\Azure_Prompts\\SeriesDatasheetInstructions_InterfaceIC.txt"),
                ("PartNumberDecodingPrompt_InterfaceIC.txt",       "..\\Azure_Prompts\\PartNumberDecodingPrompt_InterfaceIC.txt"),
                // Generic Series / Decoding
                ("SeriesDatasheetInstructions.txt",                "..\\Azure_Prompts\\SeriesDatasheetInstructions.txt"),
                ("PartNumberDecodingPrompt.txt",                   "..\\Azure_Prompts\\PartNumberDecodingPrompt.txt"),
                // Coils + EMI
                ("coil_extraction_guidelines.txt",                 "..\\Azure_Prompts\\coil_extraction_guidelines.txt"),
                ("emi_filter_extraction_guidelines.txt",           "..\\Azure_Prompts\\emi_filter_extraction_guidelines.txt"),
                // Common
                ("pitch_extraction_guidelines.txt",                "..\\Azure_Prompts\\pitch_extraction_guidelines.txt"),
                ("property_definitions.txt",                       "..\\Azure_Prompts\\property_definitions.txt"),
                ("SafetyDatasheetPrompt.txt",                      "..\\Azure_Prompts\\SafetyDatasheetPrompt.txt")
            };

            int successCount = 0;
            int failCount = 0;

            foreach (var (blobName, localFile) in filesToUpload)
            {
                bool success = UploadFile(connectionString, containerName, blobName, localFile);
                if (success)
                    successCount++;
                else
                    failCount++;
            }

            Console.WriteLine();
            Console.WriteLine("???????????????????????????????????????????????????????????");
            Console.WriteLine($" ZUSAMMENFASSUNG: {successCount} erfolgreich, {failCount} fehlgeschlagen");
            Console.WriteLine("???????????????????????????????????????????????????????????");
            Console.WriteLine();
            Console.WriteLine("NÄCHSTE SCHRITTE:");
            Console.WriteLine("1. Starten Sie die Blazor-App neu (dotnet run)");
            Console.WriteLine("2. Laden Sie ein Memory oder RF Amplifier PDF hoch");
            Console.WriteLine("3. Guidelines werden automatisch geladen!");
        }

        static bool UploadFile(string connectionString, string containerName, string blobName, string localFile)
        {
            Console.WriteLine();
            Console.WriteLine("???????????????????????????????????????????????????????????");
            Console.WriteLine($" Uploading: {blobName}");
            Console.WriteLine("???????????????????????????????????????????????????????????");
            Console.WriteLine();

            // Check if file exists
            if (!File.Exists(localFile))
            {
                Console.WriteLine($"? FEHLER: Datei nicht gefunden: {localFile}");
                Console.WriteLine($"   Überspringe {blobName}...");
                return false;
            }

            var fileInfo = new FileInfo(localFile);
            Console.WriteLine($"?? Lokale Datei: {localFile}");
            Console.WriteLine($"?? Dateigröße: {fileInfo.Length} bytes");
            Console.WriteLine();

            try
            {
                Console.WriteLine($"??  Verbinde mit Azure Storage...");
                
                var blobServiceClient = new BlobServiceClient(connectionString);
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                
                if (!containerClient.Exists())
                {
                    Console.WriteLine($"? FEHLER: Container '{containerName}' existiert nicht!");
                    return false;
                }
                
                Console.WriteLine("? Verbindung erfolgreich");
                Console.WriteLine();
                
                var blobClient = containerClient.GetBlobClient(blobName);
                
                if (blobClient.Exists())
                {
                    Console.WriteLine($"? Blob existiert bereits - wird überschrieben!");
                }
                
                Console.WriteLine("??  Uploading...");
                blobClient.Upload(localFile, overwrite: true);
                
                Console.WriteLine();
                Console.WriteLine("? UPLOAD ERFOLGREICH!");
                Console.WriteLine($"?? {blobClient.Uri}");
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("? FEHLER beim Upload:");
                Console.WriteLine(ex.Message);
                return false;
            }
        }
    }
}
