using CheckOrderConfirmationFromSupplier.Config;
using CheckOrderConfirmationFromSupplier.Services;
using CheckOrderConfirmationFromSupplier.Util;
using CheckOrderConfirmationFromSupplier.Windows;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CheckOrderConfirmationFromSupplier
{
    public partial class MainWindow : Window
    {
        // Enums für Analysemodus
        private enum AnalysisMode
        {
            OrderConfirmation,
            Datasheet
        }

        private AnalysisMode currentMode = AnalysisMode.OrderConfirmation;

        // Service-Klassen
        private AzureConfig azureConfig;
        private UiPathConfig uiPathConfig;
        private AzureAIService azureAIService;
        private DatasheetAnalysisService datasheetService;
        private UiPathOrchestratorService uiPathService;
        private PDFProcessor pdfProcessor;
        private JsonParser jsonParser;
        private AuthorizationService authorizationService;
        private PDFFileManagerService pdfFileManagerService;
        private DMSCatalogService catalogService;

        // Parallel processing
        private readonly SemaphoreSlim processingThrottle = new SemaphoreSlim(15, 15);
        private int activeProcessingTasks = 0;
        private const int MAX_PARALLEL_TASKS = 15;

        // Token-Tracking
        private int totalPromptTokens = 0;
        private int totalCompletionTokens = 0;
        private int totalProcessedPDFs = 0;

        // Speicher für aktuelles Ergebnis
        private string currentJsonResult;
        private string currentPdfFile;
        private JObject currentValidationResult; // ✅ NEU: Speichere Validation Result

        // Map für parallele Verarbeitung
        private readonly ConcurrentDictionary<string, ProcessingTask> processingTasks = new ConcurrentDictionary<string, ProcessingTask>();
        private string currentTaskId;

        private class ProcessingTask
        {
            public string PdfFile { get; set; }
            public string JsonResult { get; set; }
            public bool AzureProcessingComplete { get; set; }
            public AnalysisMode Mode { get; set; }
            public JObject ValidationResult { get; set; } // ✅ NEU: Validation Result

            public ProcessingTask(string pdfFile, AnalysisMode mode)
            {
                PdfFile = pdfFile;
                Mode = mode;
                AzureProcessingComplete = false;
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            // ✅ Warte bis Window vollständig geladen ist
            Loaded += MainWindow_Loaded;
        }

        // ✅ NEU: Wird aufgerufen wenn alle XAML-Elemente verfügbar sind
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeServices();
        }

        private void InitializeServices()
        {
            try
            {
                // Benutzerauthentifizierung zuerst prüfen
                authorizationService = new AuthorizationService();
                if (!authorizationService.CheckUserAuthorization())
                {
                    Application.Current.Shutdown();
                    return;
                }

                // Azure Diagnose beim Start
                PerformAzureDiagnosis();

                // Services initialisieren
                azureConfig = new AzureConfig();
                uiPathConfig = new UiPathConfig();

                // ✅ BEIDE Services verwenden die gleiche AzureConfig
                azureAIService = new AzureAIService(azureConfig);
                datasheetService = new DatasheetAnalysisService(azureConfig);

                // Token-Callback für beide Services setzen
                azureAIService.TokenStatsCallback += UpdateTokenStatsHandler;
                datasheetService.TokenStatsCallback += UpdateTokenStatsHandler;

                // PDF Processor wird von beiden Modi verwendet
                pdfProcessor = new PDFProcessor();
                jsonParser = new JsonParser();
                pdfFileManagerService = new PDFFileManagerService();

                // Katalog-Service für Korrektur-Dialog
                catalogService = new DMSCatalogService();
                try
                {
                    catalogService.LoadCatalogData();
                    Console.WriteLine("✓ Katalog-Service für Korrektur-Dialog geladen");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠ Katalog-Service konnte nicht geladen werden: {ex.Message}");
                }

                // UiPath Service nur initialisieren wenn Konfiguration gültig ist
                if (uiPathConfig.IsConfigurationValid())
                {
                    uiPathService = new UiPathOrchestratorService(
                        uiPathConfig.GetOrchestratorUrl(),
                        uiPathConfig.GetTenantName(),
                        uiPathConfig.GetQueueName(),
                        uiPathConfig.GetClientId(),
                        uiPathConfig.GetClientSecret(),
                        uiPathConfig.GetOrganizationUnitId()
                    );
                    Console.WriteLine("✓ UiPath Orchestrator Service initialisiert");
                }
                else
                {
                    Console.WriteLine("⚠ UiPath Konfiguration ungültig - Service nicht verfügbar");
                }

                UpdateStatusText("Bereit - Ziehen Sie eine PDF-Datei hierher oder klicken Sie 'Datei auswählen'");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler bei der Initialisierung: {ex.Message}", "Initialisierungsfehler",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        private void PerformAzureDiagnosis()
        {
            Console.WriteLine("=================== STARTUP DIAGNOSE ===================");
            try
            {
                var diagService = new RemoteConfigService("DIAGNOSE-" + Guid.NewGuid());
                diagService.DiagnoseConnection();
            }
            catch (Exception e)
            {
                Console.WriteLine($"FEHLER bei Startup-Diagnose: {e.Message}");
                Console.WriteLine(e.StackTrace);
            }
            Console.WriteLine("====================================================");
        }

        private void AnalysisMode_Changed(object sender, RoutedEventArgs e)
        {
            // ✅ Prüfe ob UI-Elemente bereits initialisiert sind
            if (uploadIcon == null || uploadText == null || resultsIcon == null || resultsTitle == null)
            {
                return; // Noch nicht bereit
            }

            if (orderConfirmationMode.IsChecked == true)
            {
                currentMode = AnalysisMode.OrderConfirmation;
                uploadIcon.Text = "☁️";
                uploadText.Text = "Auftragsbestätigungs-PDF hierher ziehen";
                resultsIcon.Text = "📋";
                resultsTitle.Text = "Auftragsbestätigungsdaten";
                UpdateStatusText("Auftragsbestätigungsmodus - Bereit");
            }
            else if (datasheetMode.IsChecked == true)
            {
                currentMode = AnalysisMode.Datasheet;
                uploadIcon.Text = "🔬";
                uploadText.Text = "Datenblatt-PDF hierher ziehen";
                resultsIcon.Text = "🧪";
                resultsTitle.Text = "Datenblatt-Analyse";
                UpdateStatusText("Datenblättermodus - Bereit");
            }
        }

        // Drag & Drop Events
        private void DragDropArea_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                var brush = new System.Windows.Media.LinearGradientBrush();
                brush.StartPoint = new Point(0, 0);
                brush.EndPoint = new Point(1, 1);
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Color.FromRgb(230, 240, 255), 0));
                brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                    System.Windows.Media.Color.FromRgb(200, 230, 255), 1));
                dragDropArea.Background = brush;
                dragDropArea.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0, 188, 242));
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void DragDropArea_DragLeave(object sender, DragEventArgs e)
        {
            var brush = new System.Windows.Media.LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(1, 1);
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(249, 250, 251), 0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(243, 244, 246), 1));
            dragDropArea.Background = brush;
            dragDropArea.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(225, 225, 225));
        }

        private void DragDropArea_Drop(object sender, DragEventArgs e)
        {
            var brush = new System.Windows.Media.LinearGradientBrush();
            brush.StartPoint = new Point(0, 0);
            brush.EndPoint = new Point(1, 1);
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(249, 250, 251), 0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(
                System.Windows.Media.Color.FromRgb(243, 244, 246), 1));
            dragDropArea.Background = brush;
            dragDropArea.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(225, 225, 225));

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    string file = files[0];
                    if (file.ToLower().EndsWith(".pdf"))
                    {
                        ProcessPDF(file);
                    }
                    else
                    {
                        MessageBox.Show("Bitte wählen Sie eine PDF-Datei aus.", "Ungültiger Dateityp",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "PDF-Dateien (*.pdf)|*.pdf",
                Title = "PDF-Datei auswählen"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                ProcessPDF(openFileDialog.FileName);
            }
        }

        private async void ProcessPDF(string pdfFilePath)
        {
            if (activeProcessingTasks >= MAX_PARALLEL_TASKS)
            {
                MessageBox.Show(
                    $"Maximal {MAX_PARALLEL_TASKS} PDFs können gleichzeitig verarbeitet werden.\n" +
                    "Bitte warten Sie, bis eine Verarbeitung abgeschlossen ist.",
                    "Verarbeitungslimit erreicht",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Nur bei Auftragsbestätigung PDF archivieren
            if (currentMode == AnalysisMode.OrderConfirmation)
            {
                UpdateStatusText($"Kopiere PDF: {Path.GetFileName(pdfFilePath)} in Archivverzeichnis...");
                bool copySuccess = await Task.Run(() => pdfFileManagerService.CopyPDFToTargetDirectory(pdfFilePath));

                if (!copySuccess)
                {
                    Console.WriteLine("⚠ PDF-Kopie fehlgeschlagen - Verarbeitung wird trotzdem fortgesetzt");
                }
                else
                {
                    Console.WriteLine($"✓ PDF erfolgreich archiviert: {Path.GetFileName(pdfFilePath)}");
                }
            }

            currentPdfFile = pdfFilePath;
            string taskId = Guid.NewGuid().ToString();
            var task = new ProcessingTask(pdfFilePath, currentMode);
            processingTasks.TryAdd(taskId, task);

            Interlocked.Increment(ref activeProcessingTasks);

            string modeText = currentMode == AnalysisMode.OrderConfirmation ? "Auftragsbestätigung" : "Datenblatt";
            UpdateStatusText($"Verarbeite {modeText}: {Path.GetFileName(pdfFilePath)} ({activeProcessingTasks}/{MAX_PARALLEL_TASKS} parallel)");
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            resetButton.IsEnabled = false;

            if (currentTaskId == null)
            {
                currentTaskId = taskId;
            }

            await Task.Run(async () =>
            {
                await processingThrottle.WaitAsync();
                try
                {
                    Console.WriteLine($"\n=== STARTE {modeText.ToUpper()}-VERARBEITUNG ===");
                    Console.WriteLine($"Task ID: {taskId}");
                    Console.WriteLine($"PDF: {Path.GetFileName(pdfFilePath)}");
                    Console.WriteLine($"Aktive Tasks: {activeProcessingTasks}");

                    string result = null;

                    // BEIDE MODI VERWENDEN JETZT DEN GLEICHEN PDFProcessor!
                    var pdfContent = pdfProcessor.ProcessPDF(pdfFilePath);

                    if (currentMode == AnalysisMode.OrderConfirmation)
                    {
                        // Order Confirmation mit eigenem Prompt (aus Prompt.txt)
                        result = await azureAIService.AnalyzeContentAsync(pdfContent);
                    }
                    else if (currentMode == AnalysisMode.Datasheet)
                    {
                        // ✅ ZWEISTUFIGER PROZESS für Datenblätter

                        // STUFE 1: Komponenten-Identifikation
                        Console.WriteLine("\n🔍 STUFE 1: Komponenten-Identifikation");
                        string identificationResult = await datasheetService.IdentifyComponentOnlyAsync(pdfContent);

                        // Parse Identifikation
                        JObject identification = null;
                        try
                        {
                            string cleanedIdentification = ExtractCleanJsonFromResponse(identificationResult);
                            identification = JObject.Parse(cleanedIdentification);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"✗ Fehler beim Parsen der Identifikation: {ex.Message}");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                MessageBox.Show($"Fehler beim Parsen der Identifikation: {ex.Message}",
                                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return;
                        }

                        // ✅ NEU: Post-Validation mit objektiven Regeln
                        Console.WriteLine("\n🔎 STARTE POST-VALIDATION DER IDENTIFIKATION...");
                        var validationService = new IdentificationValidationService(catalogService);
                        var validationResult = validationService.ValidateIdentification(identification);

                        Console.WriteLine($"✓ Validation abgeschlossen: {validationResult}");

                        if (validationResult.Issues.Count > 0)
                        {
                            Console.WriteLine("\n⚠️ VALIDATION ISSUES GEFUNDEN:");
                            foreach (var issue in validationResult.Issues)
                            {
                                Console.WriteLine(issue.ToString());
                            }
                            Console.WriteLine();
                        }

                        // Extrahiere erkannte Werte
                        string docType = identification["DocumentType"]?.Value<string>() ?? "UNKNOWN";
                        string category = identification["ComponentCategory"]?.Value<string>() ?? "";
                        string compName = identification["ComponentName"]?.Value<string>() ?? "";
                        string manufacturer = identification["ManufacturerInfo"]?.Value<string>() ?? "";

                        // ✅ FIX: Speichere ValidationResult NUR für COMPONENT (nicht für MIXTURE!)
                        JObject validationResultForDisplay = null;

                        if (!docType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase))
                        {
                            // ✅ Nur für Technical Data Sheets (COMPONENT) speichern
                            var issueDetails = new JArray();
                            foreach (var issue in validationResult.Issues)
                            {
                                issueDetails.Add(new JObject
                                {
                                    ["Severity"] = issue.Severity.ToString(),
                                    ["Field"] = issue.Field,
                                    ["Message"] = issue.Message,
                                    ["CurrentValue"] = issue.CurrentValue,
                                    ["Suggestion"] = issue.Suggestion
                                });
                            }

                            validationResultForDisplay = new JObject
                            {
                                ["ConfidenceScore"] = validationResult.ConfidenceScore,
                                ["IssueCount"] = validationResult.Issues.Count,
                                ["ErrorCount"] = validationResult.Issues.Count(i => i.Severity == IdentificationValidationService.ValidationSeverity.Error),
                                ["WarningCount"] = validationResult.Issues.Count(i => i.Severity == IdentificationValidationService.ValidationSeverity.Warning),
                                ["Issues"] = issueDetails
                            };
                        }
                        else
                        {
                            // ✅ Für MIXTURE (Safety Data Sheets): KEIN ValidationResult
                            Console.WriteLine("ℹ️ Safety Data Sheet - ValidationResult wird NICHT gespeichert");
                        }

                        // Speichere in Task
                        task.ValidationResult = validationResultForDisplay;

                        // ✅ NEU: Safety Data Sheets (MIXTURE) benötigen KEINE Bestätigung
                        //         da es keine Komponenten-Kategorie oder Part Number gibt
                        JObject confirmedIdentification = null;
                        JObject manualResistorProperties = null;
                        bool userConfirmed = false;

                        if (docType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase))
                        {
                            // ✅ Safety Data Sheet - KEINE Bestätigung erforderlich
                            Console.WriteLine("ℹ️ Safety Data Sheet erkannt - Dialog wird übersprungen");
                            confirmedIdentification = identification;
                            userConfirmed = true;
                        }
                        else
                        {
                            // ✅ Technical Data Sheet - Zeige Bestätigungsdialog
                            await Dispatcher.InvokeAsync(() =>
                            {
                                try
                                {
                                    // ✅ NEUER MODERNER DIALOG mit Part Number Unterstützung + KI-Dekodierung
                                    var identDialog = new IdentificationConfirmationDialog(
                                        identification,
                                        Path.GetFileName(pdfFilePath),
                                        datasheetService,
                                        pdfContent);

                                    identDialog.Owner = this;

                                    bool? dialogResult = identDialog.ShowDialog();

                                    if (dialogResult == true)
                                    {
                                        if (identDialog.UserChoice == IdentificationConfirmationDialog.UserDialogResult.Confirmed)
                                        {
                                            // ✅ Bestätigt - verwende erkannte Werte (mit ausgewählter Part Number)
                                            confirmedIdentification = identDialog.IdentificationData;

                                            // ✅ NEU: Prüfe ob manuelle Resistor-Properties vorhanden sind (NUR bei Resistoren!)
                                            if (identDialog.ManualResistorProperties != null &&
                                                category.Equals("Resistor", StringComparison.OrdinalIgnoreCase))
                                            {
                                                manualResistorProperties = identDialog.ManualResistorProperties;
                                                Console.WriteLine("✓ Manuelle Resistor-Properties vom Benutzer eingegeben - KI-Extraktion wird übersprungen!");
                                            }

                                            userConfirmed = true;

                                            Console.WriteLine("✓ Benutzer hat Identifikation bestätigt");
                                            if (!string.IsNullOrEmpty(identDialog.SelectedPartNumber))
                                            {
                                                Console.WriteLine($"  → Ausgewählte Herstellernummer: {identDialog.SelectedPartNumber}");
                                            }
                                        }
                                        else if (identDialog.UserChoice == IdentificationConfirmationDialog.UserDialogResult.NeedsCorrection)
                                        {
                                            // ❌ Korrektur angefordert - NUR für Komponenten-Kategorie (FREIE EINGABE)
                                            Console.WriteLine("⚠ Benutzer möchte Kategorie korrigieren");

                                            string correctedCategory = null;

                                            // Einfacher InputDialog für Kategorie (FREIE EINGABE, keine Vorschläge!)
                                            string correctionPrompt =
                                                $"📝 KOMPONENTEN-KATEGORIE KORRIGIEREN\n" +
                                                $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
                                                $"Aktuell: {category}\n\n" +
                                                $"Geben Sie die korrekte Komponenten-Kategorie ein:";

                                            var categoryDialog = new InputDialog(
                                                "Kategorie korrigieren",
                                                correctionPrompt,
                                                category);

                                            if (categoryDialog.ShowDialog() == true)
                                            {
                                                if (!string.IsNullOrWhiteSpace(categoryDialog.InputText))
                                                {
                                                    correctedCategory = categoryDialog.InputText.Trim();

                                                    // Übernehme korrigierte Kategorie
                                                    confirmedIdentification = (JObject)identification.DeepClone();
                                                    confirmedIdentification["ComponentCategory"] = correctedCategory;

                                                    // ✅ FIX: Übernehme Part Number vom Dialog!
                                                    if (!string.IsNullOrEmpty(identDialog.SelectedPartNumber))
                                                    {
                                                        confirmedIdentification["PartNumber"] = identDialog.SelectedPartNumber;
                                                        Console.WriteLine($"  Part Number: {identDialog.SelectedPartNumber}");
                                                    }

                                                    userConfirmed = true;

                                                    Console.WriteLine("✓ Benutzer hat Kategorie korrigiert:");
                                                    Console.WriteLine($"  Kategorie: {category} → {correctedCategory}");

                                                    // Markiere als korrigiert für Feedback-Learning
                                                    confirmedIdentification["WasCorrected"] = true;
                                                    confirmedIdentification["OriginalCategory"] = category;
                                                    confirmedIdentification["CorrectionTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                                                }
                                                else
                                                {
                                                    MessageBox.Show(
                                                        "Bitte geben Sie eine gültige Kategorie ein.\n\n" +
                                                        "Die Eingabe darf nicht leer sein.",
                                                        "Eingabe erforderlich",
                                                        MessageBoxButton.OK,
                                                        MessageBoxImage.Warning);
                                                }
                                            }
                                            else
                                            {
                                                // Benutzer hat Dialog abgebrochen
                                                Console.WriteLine("✗ Kategorie-Korrektur vom Benutzer abgebrochen");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // ⛔ Analyse komplett abbrechen
                                        Console.WriteLine("✗ Benutzer hat Analyse abgebrochen");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"✗ Fehler im Bestätigungsdialog: {ex.Message}");
                                    MessageBox.Show($"Fehler: {ex.Message}",
                                        "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                                }
                            });
                        }

                        if (!userConfirmed || confirmedIdentification == null)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (taskId == currentTaskId)
                                {
                                    UpdateStatusText("Analyse abgebrochen");
                                }
                            });
                            return;
                        }

                        // ✅ DEBUG: Prüfe was in confirmedIdentification steht
                        Console.WriteLine("\n═══════════════════════════════════════");
                        Console.WriteLine("DEBUG: BESTÄTIGTE IDENTIFIKATION");
                        Console.WriteLine($"Part Number: {confirmedIdentification["PartNumber"]}");
                        Console.WriteLine($"Category: {confirmedIdentification["ComponentCategory"]}");
                        Console.WriteLine($"Name: {confirmedIdentification["ComponentName"]}");
                        Console.WriteLine("═══════════════════════════════════════\n");

                        // ✅ NEU: Wenn dekodierte oder manuell eingegebene Resistor-Properties vorhanden sind, 
                        //         verwende diese STATT KI-Extraktion aus PDF!

                        if (manualResistorProperties != null)
                        {
                            Console.WriteLine("\n🔧 VERWENDE RESISTOR-PROPERTIES AUS PART NUMBER");
                            Console.WriteLine($"   Quelle: {manualResistorProperties["DataSource"]}");

                            // Füge Herstellernummer und Meta-Daten hinzu
                            manualResistorProperties["PartNumber"] = confirmedIdentification["PartNumber"];
                            manualResistorProperties["DocumentType"] = "COMPONENT";
                            manualResistorProperties["ComponentCategory"] = confirmedIdentification["ComponentCategory"]; // ✅ Verwende bestätigte Kategorie
                            manualResistorProperties["ManufacturerInfo"] = confirmedIdentification["ManufacturerInfo"];

                            // Timestamp bereits vorhanden (wird im ResistorParameterDialog gesetzt)
                            if (!manualResistorProperties.ContainsKey("InputTimestamp"))
                            {
                                manualResistorProperties["InputTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                            }

                            // Konvertiere zu JSON-String
                            result = manualResistorProperties.ToString(Newtonsoft.Json.Formatting.Indented);

                            Console.WriteLine("✓ Resistor-Daten vorbereitet (OHNE KI-Extraktion aus PDF):");
                            Console.WriteLine(result);

                            // ✅ WICHTIG: BEHALTE ValidationResult - es wurde bereits vor der Verzweigung gespeichert!
                            // task.ValidationResult wurde bereits oben gesetzt und muss nicht erneut gesetzt werden
                            Console.WriteLine($"✓ ValidationResult bleibt erhalten: Confidence = {task.ValidationResult?["ConfidenceScore"]}");
                        }
                        else
                        {
                            // STUFE 2: Detail-Extraktion mit bestätigter Identifikation (normale KI-Extraktion)
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (taskId == currentTaskId)
                                {
                                    UpdateStatusText("🔬 Stufe 2: Extrahiere Details...");
                                }
                            });

                            Console.WriteLine("\n🔬 STUFE 2: Detail-Extraktion");
                            // ✅ FIX: Verwende neue Methode mit bestätigter Identifikation (inkl. Part Number!)
                            result = await datasheetService.AnalyzeWithConfirmedIdentificationAsync(
                                pdfContent,
                                confirmedIdentification);
                        }

                        // ✅ NEU: VALIDIERUNG DER EXTRAHIERTEN PROPERTIES (nur wenn KI-Extraktion)
                        if (manualResistorProperties == null)
                        {
                            try
                            {
                                string cleanedJson = ExtractCleanJsonFromResponse(result);
                                var extractedData = JObject.Parse(cleanedJson);

                                Console.WriteLine("\n🔎 STARTE PROPERTY-VALIDIERUNG...");

                                // Validiere extrahierte Properties
                                var propertyValidationService = new PropertyValidationService();
                                var propertyValidationResult = propertyValidationService.ValidateExtractedProperties(
                                    extractedData,
                                    confirmedIdentification["ComponentCategory"]?.ToString() ?? "");

                                // Zeige Warnungen in Console
                                if (propertyValidationResult.HasIssues)
                                {
                                    Console.WriteLine($"\n⚠ VALIDIERUNGSWARNUNGEN GEFUNDEN:");
                                    Console.WriteLine($"   {propertyValidationResult.Warnings.Count} Warnungen");
                                    Console.WriteLine($"   {propertyValidationResult.Errors.Count} Fehler");
                                    Console.WriteLine();

                                    foreach (var warning in propertyValidationResult.Warnings)
                                    {
                                        Console.WriteLine($"  ⚠ {warning.PropertyName}:");
                                        Console.WriteLine($"     Aktuell: {warning.CurrentValue}");
                                        Console.WriteLine($"     Problem: {warning.Message}");
                                        Console.WriteLine($"     Hinweis: {warning.Suggestion}");
                                        Console.WriteLine();
                                    }

                                    foreach (var error in propertyValidationResult.Errors)
                                    {
                                        Console.WriteLine($"  ✗ {error.PropertyName}:");
                                        Console.WriteLine($"     Aktuell: {error.CurrentValue}");
                                        Console.WriteLine($"     Fehler: {error.Message}");
                                        Console.WriteLine();
                                    }

                                    // ✅ NEU: Füge Property-Warnings zum ValidationResult hinzu
                                    if (task.ValidationResult != null)
                                    {
                                        var propertyWarnings = new JArray();
                                        foreach (var warning in propertyValidationResult.Warnings)
                                        {
                                            propertyWarnings.Add(new JObject
                                            {
                                                ["Severity"] = "Warning",
                                                ["PropertyName"] = warning.PropertyName,
                                                ["Message"] = warning.Message,
                                                ["CurrentValue"] = warning.CurrentValue,
                                                ["Suggestion"] = warning.Suggestion
                                            });
                                        }

                                        foreach (var error in propertyValidationResult.Errors)
                                        {
                                            propertyWarnings.Add(new JObject
                                            {
                                                ["Severity"] = "Error",
                                                ["PropertyName"] = error.PropertyName,
                                                ["Message"] = error.Message,
                                                ["CurrentValue"] = error.CurrentValue,
                                                ["Suggestion"] = ""
                                            });
                                        }

                                        task.ValidationResult["PropertyWarnings"] = propertyWarnings;
                                        task.ValidationResult["PropertyWarningCount"] = propertyValidationResult.Warnings.Count;
                                        task.ValidationResult["PropertyErrorCount"] = propertyValidationResult.Errors.Count;

                                        Console.WriteLine($"✓ {propertyWarnings.Count} Property-Warnings zum ValidationResult hinzugefügt");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("✓ VALIDIERUNG ERFOLGREICH - Keine Probleme gefunden!");
                                }

                                // ✅ NEU: Zeige Validation-Warnung NACH der Analyse (nur bei niedrigem Confidence < 70%)
                                if (validationResult.ConfidenceScore < 0.7)
                                {
                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        string warningMessage = $"⚠️ AUTOMATISCHER VALIDIERUNGSHINWEIS\n\n" +
                                            $"Confidence: {validationResult.ConfidenceScore:P0}\n\n" +
                                            $"Die KI-Identifikation könnte Probleme enthalten:\n\n";

                                        foreach (var issue in validationResult.Issues.Take(5))
                                        {
                                            warningMessage += $"• {issue.Field}: {issue.Message}\n";
                                        }

                                        if (validationResult.Issues.Count > 5)
                                        {
                                            warningMessage += $"... und {validationResult.Issues.Count - 5} weitere\n";
                                        }

                                        warningMessage += $"\n💡 Bitte überprüfen Sie die extrahierten Daten sorgfältig.";

                                        MessageBox.Show(warningMessage,
                                            "Validierungs-Hinweis",
                                            MessageBoxButton.OK,
                                            MessageBoxImage.Information);
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"⚠ Fehler bei Property-Validierung: {ex.Message}");
                                Console.WriteLine("  Extraktion wird trotzdem fortgesetzt.");
                            }
                        }
                    }

                    task.JsonResult = result;
                    task.AzureProcessingComplete = true;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (taskId == currentTaskId)
                        {
                            currentJsonResult = result;
                            currentValidationResult = task.ValidationResult; // ✅ NEU: Speichere Validation Result

                            // ✅ DEBUG: Prüfe ob ValidationResult vorhanden ist
                            if (task.ValidationResult != null)
                            {
                                Console.WriteLine($"\n[MainWindow] ValidationResult wird an DisplayResult übergeben:");
                                Console.WriteLine($"  ConfidenceScore: {task.ValidationResult["ConfidenceScore"]}");
                                Console.WriteLine($"  IssueCount: {task.ValidationResult["IssueCount"]}");
                            }
                            else
                            {
                                Console.WriteLine($"\n[MainWindow] ⚠️ WARNUNG: task.ValidationResult ist NULL!");
                            }

                            DisplayResult(result, task.ValidationResult); // ✅ NEU: Übergebe Validation Result

                            if (currentMode == AnalysisMode.OrderConfirmation)
                            {
                                UpdateStatusText("Analyse abgeschlossen - sende an UiPath...");
                            }
                            else
                            {
                                UpdateStatusText("Datenblattanalyse abgeschlossen");
                            }
                        }
                    });

                    // UiPath nur bei Order Confirmation
                    if (currentMode == AnalysisMode.OrderConfirmation && uiPathService != null)
                    {
                        await SendToUiPath(taskId, result);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"✗ Fehler bei PDF-Verarbeitung (Task: {taskId}): {e.Message}");
                    Console.WriteLine(e.StackTrace);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (taskId == currentTaskId)
                        {
                            MessageBox.Show($"Fehler bei der Verarbeitung: {e.Message}", "Fehler",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            UpdateStatusText("Fehler bei der Verarbeitung");
                        }
                    });
                }
                finally
                {
                    processingThrottle.Release();
                    CleanupTask(taskId);
                }
            });
        }

        private async Task SendToUiPath(string taskId, string jsonResult)
        {
            try
            {
                Console.WriteLine($"\n=== SENDE AN UIPATH (Task: {taskId}) ===");
                string pdfFileName = Path.GetFileName(currentPdfFile);
                bool uiPathSuccess = await uiPathService.SendJsonToQueue(jsonResult, pdfFileName);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (uiPathSuccess)
                    {
                        Console.WriteLine($"✓ Erfolgreich an UiPath Orchestrator gesendet (Task: {taskId})");
                        if (taskId == currentTaskId)
                        {
                            UpdateStatusText("✓ Analyse abgeschlossen und an UiPath gesendet");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"✗ Fehler beim Senden an UiPath Orchestrator (Task: {taskId})");
                        if (taskId == currentTaskId)
                        {
                            UpdateStatusText("✗ Analyse abgeschlossen, aber UiPath-Übertragung fehlgeschlagen");
                        }
                    }
                });
            }
            catch (Exception e)
            {
                Console.WriteLine($"✗ Exception beim Senden an UiPath (Task: {taskId}): {e.Message}");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (taskId == currentTaskId)
                    {
                        UpdateStatusText("✗ Analyse abgeschlossen, aber UiPath-Fehler aufgetreten");
                    }
                });
            }
        }

        private void CleanupTask(string taskId)
        {
            processingTasks.TryRemove(taskId, out _);
            int remainingTasks = Interlocked.Decrement(ref activeProcessingTasks);

            Console.WriteLine($"Task {taskId} abgeschlossen. Verbleibende Tasks: {remainingTasks}");

            Dispatcher.Invoke(() =>
            {
                if (remainingTasks <= 0)
                {
                    activeProcessingTasks = 0;
                    progressBar.Visibility = Visibility.Collapsed;
                    resetButton.IsEnabled = true;

                    if (taskId == currentTaskId)
                    {
                        UpdateStatusText("Verarbeitung abgeschlossen - Bereit für nächste PDF");
                    }
                }
                else
                {
                    UpdateStatusText($"Verarbeitung läuft ({remainingTasks} Tasks verbleibend)");
                }
            });
        }

        private void DisplayResult(string jsonResult, JObject validationResult = null)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string fileName = Path.GetFileName(currentPdfFile);
            string modeLabel = currentMode == AnalysisMode.OrderConfirmation ? "AUFTRAGSBESTÄTIGUNG" : "DATENBLATT";

            if (!string.IsNullOrWhiteSpace(jsonResult))
            {
                try
                {
                    string separator = new string('=', 80);
                    string logEntry = "\n" + separator + "\n";
                    logEntry += $"[{timestamp}] {modeLabel} - PDF: {fileName}\n";

                    // ✅ DEBUG: Zeige ob ValidationResult vorhanden ist
                    if (validationResult != null)
                    {
                        Console.WriteLine($"\n[DisplayResult] ValidationResult vorhanden:");
                        Console.WriteLine($"  ConfidenceScore: {validationResult["ConfidenceScore"]}");
                        Console.WriteLine($"  IssueCount: {validationResult["IssueCount"]}");
                    }
                    else
                    {
                        Console.WriteLine($"\n[DisplayResult] ValidationResult ist NULL!");
                    }

                    // ✅ NEU: Zeige Confidence Score für ALLE technischen Datenblätter (COMPONENT)
                    //         NUR bei Safety Data Sheets (MIXTURE) wird kein Score angezeigt
                    if (validationResult != null && validationResult["ConfidenceScore"] != null)
                    {
                        double confidence = validationResult["ConfidenceScore"].Value<double>();

                        // ✅ FIX: Prüfe ob IssueCount-Feld existiert
                        //    Wenn IssueCount existiert (auch wenn 0), dann ist es ein COMPONENT
                        //    Nur wenn IssueCount fehlt UND confidence = 1.0, dann ist es MIXTURE
                        bool hasIssueCountField = validationResult["IssueCount"] != null;
                        bool isTechnicalDatasheet = hasIssueCountField || confidence < 1.0;

                        if (isTechnicalDatasheet)
                        {
                            int issueCount = validationResult["IssueCount"]?.Value<int>() ?? 0;
                            int errorCount = validationResult["ErrorCount"]?.Value<int>() ?? 0;
                            int warningCount = validationResult["WarningCount"]?.Value<int>() ?? 0;

                            logEntry += $"Confidence Score: {confidence:P0}\n";

                            if (issueCount > 0)
                            {
                                logEntry += $"   Hinweise: {errorCount} Fehler, {warningCount} Warnungen\n";

                                // ✅ Zeige Details der Issues
                                if (validationResult["Issues"] != null && validationResult["Issues"].HasValues)
                                {
                                    logEntry += "\n";
                                    foreach (var issue in validationResult["Issues"])
                                    {
                                        string severity = issue["Severity"]?.Value<string>() ?? "Unknown";
                                        string field = issue["Field"]?.Value<string>() ?? "Unknown";
                                        string message = issue["Message"]?.Value<string>() ?? "";
                                        string currentValue = issue["CurrentValue"]?.Value<string>() ?? "";
                                        string suggestion = issue["Suggestion"]?.Value<string>() ?? "";

                                        string icon = severity == "Error" ? "✗" : "⚠";

                                        logEntry += $"   {icon} {field}: {message}\n";
                                        if (!string.IsNullOrEmpty(currentValue))
                                            logEntry += $"      Aktuell: {currentValue}\n";
                                        if (!string.IsNullOrEmpty(suggestion))
                                            logEntry += $"      Hinweis: {suggestion}\n";
                                    }
                                }
                            }
                            else
                            {
                                // ✅ Nur bei Technical Datasheets mit 0 Issues
                                logEntry += $"   ✓ Validierung erfolgreich - Keine Probleme gefunden\n";
                            }

                            // ✅ NEU: Zeige Property-Validation Warnings (falls vorhanden)
                            if (validationResult["PropertyWarnings"] != null && validationResult["PropertyWarnings"].HasValues)
                            {
                                int propWarningCount = validationResult["PropertyWarningCount"]?.Value<int>() ?? 0;
                                int propErrorCount = validationResult["PropertyErrorCount"]?.Value<int>() ?? 0;

                                logEntry += "\nPROPERTY-VALIDIERUNG:\n";
                                logEntry += $"   Hinweise: {propErrorCount} Fehler, {propWarningCount} Warnungen\n\n";

                                foreach (var propWarning in validationResult["PropertyWarnings"])
                                {
                                    string severity = propWarning["Severity"]?.Value<string>() ?? "Unknown";
                                    string propertyName = propWarning["PropertyName"]?.Value<string>() ?? "Unknown";
                                    string message = propWarning["Message"]?.Value<string>() ?? "";
                                    string currentValue = propWarning["CurrentValue"]?.Value<string>() ?? "";
                                    string suggestion = propWarning["Suggestion"]?.Value<string>() ?? "";

                                    string icon = severity == "Error" ? "✗" : "⚠";

                                    logEntry += $"   {icon} {propertyName}: {message}\n";
                                    if (!string.IsNullOrEmpty(currentValue))
                                        logEntry += $"      Aktuell: {currentValue}\n";
                                    if (!string.IsNullOrEmpty(suggestion))
                                        logEntry += $"      Hinweis: {suggestion}\n";
                                }
                            }
                        }
                        // ✅ FIX: Für MIXTURE (Safety Data Sheets) wird NICHTS angezeigt
                        // Kein "Keine Validierungsprobleme", kein Score, nichts!
                    }
                    else
                    {
                        Console.WriteLine($"[DisplayResult] Kein ValidationResult verfügbar - Score kann nicht angezeigt werden");
                    }

                    logEntry += separator + "\n";

                    string cleanedJson = ExtractCleanJsonFromResponse(jsonResult);
                    var items = jsonParser.ParseJsonToDisplayItems(cleanedJson);
                    logEntry += string.Join("\n", items.Select(i => i.ToString()));
                    logEntry += "\n" + separator + "\n";

                    jsonResultArea.Text += logEntry;
                    jsonResultArea.ScrollToEnd();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Fehler beim Formatieren des JSON: {e.Message}");
                    string logEntry = $"\n[{timestamp}] {modeLabel} - {fileName}:\n";
                    logEntry += ExtractCleanJsonFromResponse(jsonResult) + "\n";
                    jsonResultArea.Text += logEntry;
                    jsonResultArea.ScrollToEnd();
                }
            }
            else
            {
                string logEntry = $"\n[{timestamp}] {modeLabel} - {fileName}: Keine Ergebnisse erhalten.\n";
                jsonResultArea.Text += logEntry;
                jsonResultArea.ScrollToEnd();
            }
        }

        private string ExtractCleanJsonFromResponse(string azureResponse)
        {
            try
            {
                if (azureResponse.Contains("```json"))
                {
                    int startIndex = azureResponse.IndexOf("```json") + 7;
                    int endIndex = azureResponse.LastIndexOf("```");
                    if (endIndex > startIndex)
                    {
                        return azureResponse.Substring(startIndex, endIndex - startIndex).Trim();
                    }
                }
                else if (azureResponse.Contains("```"))
                {
                    int startIndex = azureResponse.IndexOf("```") + 3;
                    int endIndex = azureResponse.LastIndexOf("```");
                    if (endIndex > startIndex)
                    {
                        return azureResponse.Substring(startIndex, endIndex - startIndex).Trim();
                    }
                }
                return azureResponse.Trim();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Fehler beim Extrahieren von JSON: {e.Message}");
                return azureResponse;
            }
        }

        private void UpdateStatusText(string status)
        {
            Dispatcher.Invoke(() =>
            {
                statusLabel.Text = status;
                Console.WriteLine($"Status: {status}");
            });
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(currentJsonResult))
            {
                try
                {
                    string cleanedJson = ExtractCleanJsonFromResponse(currentJsonResult);
                    Clipboard.SetText(cleanedJson);
                    UpdateStatusText("✓ JSON in Zwischenablage kopiert");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Fehler beim Kopieren: {ex.Message}", "Fehler",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Keine Ergebnisse zum Kopieren vorhanden.", "Hinweis",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Möchten Sie die Anwendung zurücksetzen?\n\n" +
                "Dies löscht alle Ergebnisse und setzt die Token-Statistiken zurück.",
                "Zurücksetzen bestätigen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                jsonResultArea.Clear();
                currentJsonResult = null;
                currentPdfFile = null;
                currentTaskId = null;
                currentValidationResult = null; // ✅ NEU: Reset Validation Result

                totalPromptTokens = 0;
                totalCompletionTokens = 0;
                totalProcessedPDFs = 0;

                tokenStatsLabel.Text = "Tokens: 0/0 (∅ 0) | PDFs: 0 | ∅ 0 Tokens/PDF";

                UpdateStatusText("Zurückgesetzt - Bereit für neue Analyse");
            }
        }

        private void UpdateTokenStatsHandler(int promptTokens, int completionTokens)
        {
            totalPromptTokens += promptTokens;
            totalCompletionTokens += completionTokens;
            totalProcessedPDFs++;

            int totalTokens = totalPromptTokens + totalCompletionTokens;
            double avgTokensPerPDF = totalProcessedPDFs > 0 ? (double)totalTokens / totalProcessedPDFs : 0;

            Dispatcher.Invoke(() =>
            {
                int totalPrompt = totalPromptTokens;
                int totalCompletion = totalCompletionTokens;
                int processedPDFs = totalProcessedPDFs;

                if (tokenStatsLabel != null)
                {
                    tokenStatsLabel.Text = $"Tokens: {totalPrompt:N0}/{totalCompletion:N0} (∅{totalTokens:N0}) | PDFs: {processedPDFs} | ∅ {avgTokensPerPDF:F0} Tokens/PDF";
                }
            });

            Console.WriteLine("\n=== TOKEN-STATISTIKEN ===");
            Console.WriteLine($"Diese PDF: Prompt={promptTokens:N0}, Completion={completionTokens:N0}, Total={promptTokens + completionTokens:N0}");
            Console.WriteLine($"Gesamt: Prompt={totalPromptTokens:N0}, Completion={totalCompletionTokens:N0}, Total={totalTokens:N0}");
            Console.WriteLine($"Verarbeitete PDFs: {totalProcessedPDFs}");
            Console.WriteLine($"Durchschnitt pro PDF: {avgTokensPerPDF:F1} Tokens");
            Console.WriteLine("========================");
        }

        private void ShowCorrectionDialog(string jsonResult, string pdfFileName)
        {
            try
            {
                var correctionWindow = new CorrectionWindow(jsonResult, pdfFileName, catalogService);

                if (correctionWindow.ShowDialog() == true && correctionWindow.WasCorrected)
                {
                    var correctedJson = correctionWindow.CorrectedJson;
                    currentJsonResult = correctedJson.ToString();

                    // Zeige korrigiertes Ergebnis
                    DisplayCorrectedCategoryResult(correctedJson, pdfFileName);

                    UpdateStatusText("✓ Korrektur angewendet");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Fehler im Korrektur-Dialog: {ex.Message}");
                MessageBox.Show($"Fehler beim Öffnen des Korrektur-Dialogs: {ex.Message}",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DisplayCorrectedCategoryResult(JObject correctedJson, string pdfFileName)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string separator = new string('=', 80);

            string logEntry = "\n" + separator + "\n";
            logEntry += $"[{timestamp}] KORRIGIERTES DATENBLATT - PDF: {pdfFileName}\n";
            logEntry += separator + "\n";
            logEntry += $"Dokumenttyp: {correctedJson["DocumentType"]}\n";
            logEntry += $"Kategorie: {correctedJson["ComponentCategory"]}\n";
            logEntry += $"Komponente: {correctedJson["ComponentName"]}\n";
            logEntry += $"Hersteller: {correctedJson["ManufacturerInfo"]}\n";

            string originalCategory = correctedJson["OriginalCategory"]?.Value<string>();
            if (!string.IsNullOrEmpty(originalCategory))
            {
                logEntry += $"Original-Kategorie: {originalCategory}\n";
            }

            string note = correctedJson["CorrectionNote"]?.Value<string>();
            if (!string.IsNullOrEmpty(note))
            {
                logEntry += $"Notiz: {note}\n";
            }

            logEntry += $"Korrigiert am: {correctedJson["CorrectionTimestamp"]}\n";
            logEntry += separator + "\n";

            jsonResultArea.Text += logEntry;
            jsonResultArea.ScrollToEnd();
        }

        private void CorrectionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(currentJsonResult))
            {
                MessageBox.Show(
                    "Keine Datenblatt-Analyse verfügbar.\n\n" +
                    "Bitte analysieren Sie zuerst ein Datenblatt.",
                    "Keine Daten",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                // ✅ NEU: Prüfe ob es ein Safety Data Sheet (MIXTURE) ist
                string cleanedJson = ExtractCleanJsonFromResponse(currentJsonResult);
                var jsonData = JObject.Parse(cleanedJson);
                string docType = jsonData["DocumentType"]?.Value<string>() ?? "";

                if (docType.Equals("MIXTURE", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "Korrektur nicht möglich für Sicherheitsdatenblätter.\n\n" +
                        "Safety Data Sheets (MIXTURE) haben keine Komponenten-Kategorie, " +
                        "die korrigiert werden kann.\n\n" +
                        "Korrekturen sind nur für technische Datenblätter (COMPONENT) möglich.",
                        "Nicht verfügbar",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                string pdfFileName = !string.IsNullOrEmpty(currentPdfFile)
                    ? Path.GetFileName(currentPdfFile)
                    : "unknown.pdf";

                ShowCorrectionDialog(currentJsonResult, pdfFileName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Fehler beim Öffnen des Korrektur-Dialogs: {ex.Message}");
                MessageBox.Show(
                    $"Fehler beim Öffnen des Korrektur-Dialogs:\n\n{ex.Message}",
                    "Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}

