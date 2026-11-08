using CheckOrderConfirmationFromSupplier.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Windows;

namespace CheckOrderConfirmationFromSupplier.Windows
{
    /// <summary>
    /// Identification Confirmation Dialog
    /// 
    /// Dialog for confirming and refining AI-identified component information (Stage 1 of datasheet analysis).
    /// Provides two-strategy part number decoding for resistors and allows manual corrections.
    /// 
    /// Features:
    /// - Display AI-identified component information
    /// - Part number input and validation
    /// - Two-strategy automatic decoding:
    ///   1. AI-based decoding from datasheet legend
    ///   2. Manufacturer-specific pattern rules (fallback)
    /// - Manual parameter input if decoding fails
    /// - Three user choices: Confirm, Correct, Cancel
    /// 
    /// Workflow:
    /// 1. AI identifies component (Stage 1)
    /// 2. User reviews identification in this dialog
    /// 3. For resistors: User enters/confirms part number
    /// 4. System attempts automatic decoding (2 strategies)
    /// 5. If successful: User confirms decoded values
    /// 6. If failed: User enters values manually
    /// 7. Dialog returns result to main workflow
    /// 
    /// Special Handling:
    /// - Resistors: Triggers part number decoding + parameter extraction
    /// - Non-resistors: Simple confirmation (part number optional)
    /// - Part number can be from "PartNumber" field or "PartNumbers" array
    /// 
    /// Integration Points:
    /// - DatasheetAnalysisService (AI decoding)
    /// - PartNumberDecoderService (pattern decoding)
    /// - ResistorParameterDialog (manual/confirmed parameters)
    /// </summary>
    public partial class IdentificationConfirmationDialog : Window
    {
        #region Enums

        /// <summary>
        /// Result of user interaction with the dialog.
        /// </summary>
        public enum UserDialogResult
        {
            /// <summary>User confirmed the identification as correct</summary>
            Confirmed,
            
            /// <summary>User wants to correct the category</summary>
            NeedsCorrection,
            
            /// <summary>User cancelled the operation</summary>
            Cancelled
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the user's choice from the dialog.
        /// </summary>
        public UserDialogResult UserChoice { get; private set; }

        /// <summary>
        /// Gets the identification data (potentially updated with part number).
        /// </summary>
        public JObject IdentificationData { get; private set; }

        /// <summary>
        /// Gets the selected/entered part number.
        /// Set when user confirms or corrects identification.
        /// </summary>
        public string SelectedPartNumber { get; private set; }

        /// <summary>
        /// Gets the manually entered or decoded resistor properties.
        /// Only set for resistor components after successful parameter input.
        /// Contains: ResistaOhm, TolerancePct, PowerW, PackageType, etc.
        /// </summary>
        public JObject ManualResistorProperties { get; private set; }

        #endregion

        #region Fields

        private PartNumberDecoderService _partNumberDecoder;
        private DatasheetAnalysisService _datasheetService;
        private PDFProcessor.PDFContent _pdfContent;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the identification confirmation dialog.
        /// </summary>
        /// <param name="identification">AI-identified component information</param>
        /// <param name="pdfFileName">Name of the analyzed PDF file</param>
        /// <param name="datasheetService">Optional: Service for AI-based part number decoding</param>
        /// <param name="pdfContent">Optional: PDF content for AI decoding</param>
        public IdentificationConfirmationDialog(
            JObject identification, 
            string pdfFileName, 
            DatasheetAnalysisService datasheetService = null, 
            PDFProcessor.PDFContent pdfContent = null)
        {
            InitializeComponent();
            
            IdentificationData = identification;
            _partNumberDecoder = new PartNumberDecoderService();
            _datasheetService = datasheetService;
            _pdfContent = pdfContent;

            // Populate UI labels with identified information
            pdfNameLabel.Text = pdfFileName;
            docTypeLabel.Text = identification["DocumentType"]?.Value<string>() ?? "UNKNOWN";
            categoryLabel.Text = identification["ComponentCategory"]?.Value<string>() ?? "";
            componentNameLabel.Text = identification["ComponentName"]?.Value<string>() ?? "";
            manufacturerLabel.Text = identification["ManufacturerInfo"]?.Value<string>() ?? "";
            
            // Extract and display part number
            string detectedPartNumber = ExtractPartNumber(identification);
            if (!string.IsNullOrEmpty(detectedPartNumber))
            {
                partNumberTextBox.Text = detectedPartNumber;
            }

            // Configure part number input based on component type
            string category = identification["ComponentCategory"]?.Value<string>() ?? "";
            bool isResistor = IsResistor(category);
            partNumberInputGrid.Visibility = Visibility.Visible;
            partNumberTextBox.IsReadOnly = !isResistor;

            // Focus part number field for resistors (user likely needs to confirm/edit)
            if (isResistor)
                partNumberTextBox.Focus();
        }

        #endregion

        #region Part Number Extraction

        /// <summary>
        /// Extracts part number from identification JSON.
        /// Tries two sources:
        /// 1. "PartNumber" field (single string)
        /// 2. "PartNumbers" array (takes first entry)
        /// 
        /// Filters out "nicht vorhanden" placeholder values.
        /// </summary>
        /// <param name="identification">Identification JSON object</param>
        /// <returns>Part number string or empty if not found</returns>
        private string ExtractPartNumber(JObject identification)
        {
            // Try single PartNumber field first
            var partNumberToken = identification["PartNumber"];
            if (partNumberToken != null && partNumberToken.Type == JTokenType.String)
            {
                string partNumber = partNumberToken.Value<string>();
                if (!string.IsNullOrWhiteSpace(partNumber) && partNumber != "nicht vorhanden")
                {
                    return partNumber.Trim();
                }
            }

            // Fallback: Try PartNumbers array
            var partNumbersToken = identification["PartNumbers"];
            if (partNumbersToken != null && partNumbersToken.Type == JTokenType.Array)
            {
                var array = (JArray)partNumbersToken;
                if (array.Count > 0)
                {
                    string partNumber = array[0].Value<string>();
                    if (!string.IsNullOrWhiteSpace(partNumber) && partNumber != "nicht vorhanden")
                    {
                        return partNumber.Trim();
                    }
                }
            }

            return string.Empty;
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles Confirm button click.
        /// Routes to resistor-specific or generic confirmation flow.
        /// </summary>
        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            string componentCategory = IdentificationData["ComponentCategory"]?.Value<string>() ?? "";

            if (IsResistor(componentCategory))
            {
                HandleResistorConfirmation(componentCategory);
            }
            else
            {
                HandleNonResistorConfirmation();
            }
        }

        /// <summary>
        /// Handles Cancel button click.
        /// Closes dialog and signals cancellation to caller.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            UserChoice = UserDialogResult.Cancelled;
            this.DialogResult = false;
            Close();
        }

        /// <summary>
        /// Handles Correct button click.
        /// Signals that user wants to correct the category.
        /// Preserves entered part number if resistor.
        /// </summary>
        private void Correct_Click(object sender, RoutedEventArgs e)
        {
            string componentCategory = IdentificationData["ComponentCategory"]?.Value<string>() ?? "";

            // Save part number for resistors before correction
            if (IsResistor(componentCategory))
            {
                string enteredPartNumber = partNumberTextBox.Text?.Trim();

                if (!string.IsNullOrWhiteSpace(enteredPartNumber))
                {
                    SelectedPartNumber = enteredPartNumber;
                    IdentificationData["PartNumber"] = enteredPartNumber;
                    Console.WriteLine($"Part Number gespeichert für Korrektur: {enteredPartNumber}");
                }
            }

            UserChoice = UserDialogResult.NeedsCorrection;
            this.DialogResult = true;
            Close();
        }

        #endregion

        #region Resistor-Specific Confirmation Flow

        /// <summary>
        /// Handles confirmation flow for resistor components.
        /// 
        /// Process:
        /// 1. Validate part number is entered
        /// 2. Attempt AI-based decoding from datasheet legend
        /// 3. Fallback to manufacturer-specific pattern rules
        /// 4. Show decoded parameters for confirmation OR manual input dialog
        /// 
        /// Two-Strategy Decoding:
        /// Strategy 1 (AI): Analyzes datasheet legend/breakdown table (requires PDF content)
        /// Strategy 2 (Rules): Pattern matching against known manufacturer formats (Yageo, Vishay, etc.)
        /// </summary>
        private void HandleResistorConfirmation(string componentCategory)
        {
            string enteredPartNumber = partNumberTextBox.Text?.Trim();

            // Validate part number is present
            if (string.IsNullOrWhiteSpace(enteredPartNumber))
            {
                MessageBox.Show(
                    "Bitte geben Sie die Herstellernummer ein.\n\n" +
                    "Die Herstellernummer ist erforderlich für die korrekte\n" +
                    "Extraktion der technischen Parameter.",
                    "Herstellernummer erforderlich",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                partNumberTextBox.Focus();
                return;
            }

            // Save part number to identification data
            SelectedPartNumber = enteredPartNumber;
            IdentificationData["PartNumber"] = enteredPartNumber;

            Console.WriteLine("\nRESISTOR ERKANNT - Starte Part Number Dekodierung...");
            Console.WriteLine($"   Part Number: {enteredPartNumber}");
            Console.WriteLine($"   Kategorie: {componentCategory}");

            string manufacturer = IdentificationData["ManufacturerInfo"]?.Value<string>() ?? "";
            JObject decodedProperties = null;

            // Strategy 1: AI-based decoding from datasheet legend
            if (_datasheetService != null && _pdfContent != null)
            {
                Console.WriteLine("   Methode 1: KI-basierte Dekodierung aus Datenblatt-Legende");
                decodedProperties = TryAIDecoding(enteredPartNumber, manufacturer, componentCategory);
                
                if (decodedProperties != null)
                {
                    decodedProperties["DecodingSource"] = "AI Datasheet Legend";
                }
            }

            // Strategy 2: Manufacturer-specific pattern rules (fallback)
            if (decodedProperties == null)
            {
                Console.WriteLine("   Methode 2: Herstellerspezifische Dekodierungsregeln");
                decodedProperties = _partNumberDecoder.DecodePartNumber(
                    enteredPartNumber,
                    manufacturer,
                    componentCategory);

                if (decodedProperties != null)
                {
                    decodedProperties["DecodingSource"] = "Manufacturer-Specific Rules";
                }
            }

            // Process result
            if (decodedProperties != null)
            {
                HandleSuccessfulDecoding(decodedProperties, enteredPartNumber);
            }
            else
            {
                HandleFailedDecoding(enteredPartNumber);
            }
        }

        /// <summary>
        /// Attempts AI-based part number decoding from datasheet legend.
        /// Analyzes the datasheet PDF for part number breakdown tables and extracts parameters.
        /// Shows progress window during analysis (30 second timeout).
        /// </summary>
        /// <param name="partNumber">Part number to decode</param>
        /// <param name="manufacturer">Manufacturer name</param>
        /// <param name="category">Component category</param>
        /// <returns>Decoded properties or null if decoding fails</returns>
        private JObject TryAIDecoding(string partNumber, string manufacturer, string category)
        {
            // Create progress window
            var progressWindow = new Window
            {
                Title = "Part Number wird dekodiert...",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Analysiere Datenblatt-Legende...\n\n" +
                           "Die KI sucht nach der Part Number Breakdown Tabelle\n" +
                           "und dekodiert die Herstellernummer automatisch.",
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20)
                }
            };

            // Start AI decoding task
            var task = System.Threading.Tasks.Task.Run(async () =>
            {
                return await _datasheetService.DecodePartNumberFromDatasheetAsync(
                    _pdfContent,
                    partNumber,
                    manufacturer,
                    category);
            });

            progressWindow.Show();

            // Wait for result with timeout
            JObject result = null;
            if (task.Wait(TimeSpan.FromSeconds(30)))
            {
                result = task.Result;
                progressWindow.Close();
                
                if (result != null)
                {
                    Console.WriteLine("KI-Dekodierung erfolgreich!");
                }
                else
                {
                    Console.WriteLine("KI-Dekodierung fehlgeschlagen - verwende Fallback");
                }
            }
            else
            {
                progressWindow.Close();
                Console.WriteLine("KI-Dekodierung Timeout - verwende Fallback");
            }

            return result;
        }

        /// <summary>
        /// Handles successful part number decoding.
        /// Shows decoded parameters in ResistorParameterDialog for user confirmation.
        /// If user confirms, stores parameters and closes with success.
        /// </summary>
        private void HandleSuccessfulDecoding(JObject decodedProperties, string partNumber)
        {
            Console.WriteLine("Part Number erfolgreich dekodiert!");
            Console.WriteLine($"  Quelle: {decodedProperties["DecodingSource"]}");
            Console.WriteLine($"  Widerstand: {decodedProperties["ResistaOhm"]} Ohm");
            Console.WriteLine($"  Toleranz: ±{decodedProperties["TolerancePct"]}%");
            Console.WriteLine($"  Leistung: {decodedProperties["PowerW"]} W");
            Console.WriteLine($"  Gehäuse: {decodedProperties["PackageType"]}");

            // Show decoded parameters in confirmation dialog
            var resistorDialog = new ResistorParameterDialog(partNumber, decodedProperties);
            bool? result = resistorDialog.ShowDialog();

            if (result == true && resistorDialog.WasConfirmed)
            {
                Console.WriteLine("Dekodierte Resistor-Parameter vom User bestätigt");
                ManualResistorProperties = resistorDialog.ResistorProperties;
                UserChoice = UserDialogResult.Confirmed;
                this.DialogResult = true;
                Close();
            }
            else
            {
                Console.WriteLine("Dekodierte Parameter vom User abgelehnt");
            }
        }

        /// <summary>
        /// Handles failed part number decoding.
        /// Opens ResistorParameterDialog for manual parameter input.
        /// </summary>
        private void HandleFailedDecoding(string partNumber)
        {
            Console.WriteLine("Automatische Dekodierung fehlgeschlagen (beide Methoden)");
            Console.WriteLine("  Öffne manuelle Eingabe...");

            // Open manual input dialog
            var resistorDialog = new ResistorParameterDialog(partNumber);
            bool? result = resistorDialog.ShowDialog();

            if (result == true && resistorDialog.WasConfirmed)
            {
                Console.WriteLine("Resistor-Parameter manuell eingegeben:");
                Console.WriteLine($"  - Widerstand: {resistorDialog.ResistorProperties["ResistaOhm"]}");
                Console.WriteLine($"  - Toleranz: {resistorDialog.ResistorProperties["TolerancePct"]}");
                Console.WriteLine($"  - Leistung: {resistorDialog.ResistorProperties["PowerW"]}");

                ManualResistorProperties = resistorDialog.ResistorProperties;
                UserChoice = UserDialogResult.Confirmed;
                this.DialogResult = true;
                Close();
            }
            else
            {
                Console.WriteLine("Resistor-Parameter-Eingabe abgebrochen");
            }
        }

        #endregion

        #region Non-Resistor Confirmation Flow

        /// <summary>
        /// Handles confirmation flow for non-resistor components.
        /// Simple confirmation without part number decoding.
        /// Part number is optional but will be saved if entered.
        /// </summary>
        private void HandleNonResistorConfirmation()
        {
            string enteredPartNumber = partNumberTextBox.Text?.Trim();

            // Save part number if present
            if (!string.IsNullOrWhiteSpace(enteredPartNumber))
            {
                SelectedPartNumber = enteredPartNumber;
                IdentificationData["PartNumber"] = enteredPartNumber;
            }

            UserChoice = UserDialogResult.Confirmed;
            this.DialogResult = true;
            Close();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Checks if component category is a resistor type.
        /// Matches any category containing "Resistor" or "Widerstand".
        /// Examples: "Resistor", "Resistor Constant", "Resistor Network", etc.
        /// </summary>
        private bool IsResistor(string componentCategory)
        {
            if (string.IsNullOrWhiteSpace(componentCategory))
                return false;

            return componentCategory.IndexOf("Resistor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   componentCategory.IndexOf("Widerstand", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion
    }
}
