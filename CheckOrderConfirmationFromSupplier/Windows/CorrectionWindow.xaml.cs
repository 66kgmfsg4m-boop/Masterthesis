using CheckOrderConfirmationFromSupplier.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CheckOrderConfirmationFromSupplier.Windows
{
    /// <summary>
    /// Correction Window for Manual Category Adjustment
    /// 
    /// Allows users to manually correct AI-identified component categories when validation
    /// detects potential errors or low confidence scores. Primarily used for datasheet analysis.
    /// 
    /// Features:
    /// - Category selection from DMS catalog
    /// - Document type selection (COMPONENT/MIXTURE)
    /// - Manufacturer information editing
    /// - Part number input for resistors
    /// - Property count display per category
    /// - Correction notes and timestamps
    /// 
    /// Workflow:
    /// 1. User reviews AI-identified category
    /// 2. Selects correct category from dropdown
    /// 3. Optionally adds correction note
    /// 4. System marks data as manually corrected
    /// 5. Corrected data replaces original identification
    /// 
    /// Use Cases:
    /// - AI misidentified component type (e.g., "Amplifier" instead of "OP Amplifier")
    /// - Low confidence score (< 70%)
    /// - Generic category detected by validation rules
    /// - User wants to override AI decision
    /// </summary>
    public partial class CorrectionWindow : Window
    {
        #region Fields

        private readonly DMSCatalogService _catalogService;
        private JObject _originalJson;
        private string _pdfFileName;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the corrected JSON data after user applies changes.
        /// Contains corrected category, manufacturer info, and metadata.
        /// </summary>
        public JObject CorrectedJson { get; private set; }

        /// <summary>
        /// Indicates whether user applied corrections (true) or cancelled (false).
        /// </summary>
        public bool WasCorrected { get; private set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the correction window with original AI results.
        /// </summary>
        /// <param name="jsonResult">Original JSON result from AI analysis</param>
        /// <param name="pdfFileName">Name of the analyzed PDF file</param>
        /// <param name="catalogService">DMS catalog service for loading available categories</param>
        public CorrectionWindow(string jsonResult, string pdfFileName, DMSCatalogService catalogService)
        {
            InitializeComponent();

            _catalogService = catalogService;
            _pdfFileName = pdfFileName;

            try
            {
                // Parse original JSON and initialize form fields
                _originalJson = JObject.Parse(ExtractCleanJson(jsonResult));
                PopulateFields();
                LoadCategories();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Laden der Daten: {ex.Message}",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        #endregion

        #region JSON Processing

        /// <summary>
        /// Extracts clean JSON from Azure OpenAI response.
        /// Removes markdown code blocks and extracts JSON object.
        /// 
        /// Handles formats:
        /// - ```json ... ``` (markdown code block)
        /// - Plain JSON with { }
        /// - Mixed text with embedded JSON
        /// </summary>
        /// <param name="response">Raw Azure OpenAI response</param>
        /// <returns>Clean JSON string</returns>
        private string ExtractCleanJson(string response)
        {
            // Try to extract from markdown code block
            if (response.Contains("```json"))
            {
                int startJson = response.IndexOf("```json") + 7;
                int endJson = response.IndexOf("```", startJson);
                if (startJson > 7 && endJson > startJson)
                {
                    return response.Substring(startJson, endJson - startJson).Trim();
                }
            }

            // Fallback: Extract JSON by finding outermost braces
            if (response.Contains("{") && response.Contains("}"))
            {
                int startBrace = response.IndexOf('{');
                int endBrace = response.LastIndexOf('}') + 1;
                if (endBrace > startBrace)
                {
                    return response.Substring(startBrace, endBrace - startBrace).Trim();
                }
            }

            // If no special format detected, return as-is
            return response;
        }

        #endregion

        #region Form Initialization

        /// <summary>
        /// Populates form fields with data from original AI identification.
        /// Sets document type, category, manufacturer, and part number (for resistors).
        /// </summary>
        private void PopulateFields()
        {
            // Display PDF file name
            pdfNameLabel.Text = $"PDF: {_pdfFileName}";

            // Set document type (COMPONENT or MIXTURE)
            string docType = _originalJson["DocumentType"]?.Value<string>() ?? "COMPONENT";
            foreach (ComboBoxItem item in documentTypeComboBox.Items)
            {
                if (item.Tag.ToString() == docType)
                {
                    documentTypeComboBox.SelectedItem = item;
                    break;
                }
            }

            // Set component category
            string category = _originalJson["ComponentCategory"]?.Value<string>() ?? "";
            categoryComboBox.Text = category;

            // Set manufacturer information
            manufacturerTextBox.Text = _originalJson["ManufacturerInfo"]?.Value<string>() ?? "";

            // Set part number (only visible for resistors)
            string partNumber = _originalJson["PartNumber"]?.Value<string>() ?? "";
            partNumberTextBox.Text = partNumber;

            // Show/hide part number panel based on category
            UpdatePartNumberVisibility(category);
        }

        /// <summary>
        /// Loads all available component categories from DMS catalog.
        /// 
        /// Process:
        /// 1. Retrieves all components from catalog
        /// 2. Extracts unique categories from DisplayName and ComponentName
        /// 3. Checks property count for each category
        /// 4. Displays categories with property count (e.g., "OP Amplifier (18 Properties)")
        /// 5. Sorts categories alphabetically
        /// 
        /// Note: Uses both DisplayName (Column D) and ComponentName (Column E) to ensure
        /// all category variants are available (e.g., "RF Amplifier" vs "OP Amplifier").
        /// </summary>
        private void LoadCategories()
        {
            try
            {
                var components = _catalogService.GetAllComponents();

                // Collect unique categories from both DisplayName and ComponentName
                var categories = new HashSet<string>();

                foreach (var component in components)
                {
                    // Column D: DisplayName (e.g., "RF Amplifier", "OP Amplifier")
                    if (!string.IsNullOrWhiteSpace(component.DisplayName))
                    {
                        categories.Add(component.DisplayName);
                    }

                    // Column E: ComponentName (if different from DisplayName)
                    if (!string.IsNullOrWhiteSpace(component.ComponentName) &&
                        component.ComponentName != component.DisplayName)
                    {
                        categories.Add(component.ComponentName);
                    }
                }

                // Build category list with property counts
                var categoriesWithProperties = new List<string>();

                foreach (var category in categories.OrderBy(c => c))
                {
                    // Find matching component in catalog
                    var matchingComponent = components.FirstOrDefault(c =>
                        c.DisplayName == category || c.ComponentName == category);

                    if (matchingComponent != null)
                    {
                        // Get properties for this category (try multiple lookup strategies)
                        var props = _catalogService.GetPropertiesForComponent(matchingComponent.FullPath);
                        if (props.Count == 0)
                            props = _catalogService.GetPropertiesForComponent(matchingComponent.InternalName);

                        // Format: "Category Name (X Properties)" or just "Category Name"
                        string displayText = props.Count > 0
                            ? $"{category} ({props.Count} Properties)"
                            : category;

                        categoriesWithProperties.Add(displayText);
                    }
                    else
                    {
                        categoriesWithProperties.Add(category);
                    }
                }

                // Populate dropdown
                categoryComboBox.Items.Clear();
                foreach (var category in categoriesWithProperties)
                {
                    categoryComboBox.Items.Add(category);
                }

                Console.WriteLine($"{categoriesWithProperties.Count} spezifische Kategorien geladen");

                // Debug: Log amplifier categories (common correction case)
                var amplifierCategories = categoriesWithProperties.Where(c =>
                    c.ToLower().Contains("amplifier")).ToList();

                if (amplifierCategories.Count > 0)
                {
                    Console.WriteLine("\n  Amplifier-Kategorien:");
                    foreach (var cat in amplifierCategories)
                    {
                        Console.WriteLine($"    - {cat}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler beim Laden der Kategorien: {ex.Message}");
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles catalog refresh button click.
        /// Reloads catalog data from disk and refreshes category dropdown.
        /// Useful when catalog file is updated externally.
        /// </summary>
        private void RefreshCategories_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _catalogService.LoadCatalogData();
                LoadCategories();
                MessageBox.Show("Kategorien erfolgreich aktualisiert!",
                    "Erfolg", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Aktualisieren: {ex.Message}",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles category selection change.
        /// Updates part number field visibility based on selected category.
        /// Part number is only relevant for resistor components.
        /// </summary>
        private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Extract clean category name (remove property count suffix)
            string categoryText = categoryComboBox.Text.Trim();
            int propCountIndex = categoryText.IndexOf(" (");
            if (propCountIndex > 0)
            {
                categoryText = categoryText.Substring(0, propCountIndex).Trim();
            }

            UpdatePartNumberVisibility(categoryText);
        }

        /// <summary>
        /// Handles Apply button click.
        /// Creates corrected JSON object with user changes and closes dialog.
        /// 
        /// Process:
        /// 1. Extract clean category name (remove property count)
        /// 2. Create corrected JSON with metadata
        /// 3. Add part number if resistor category
        /// 4. Preserve original ComponentName
        /// 5. Mark as manually corrected with timestamp
        /// </summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Get selected document type
                var selectedDocType = (documentTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "COMPONENT";

                // Extract clean category name (remove property count suffix)
                // Example: "OP Amplifier (18 Properties)" -> "OP Amplifier"
                string categoryText = categoryComboBox.Text.Trim();
                int propCountIndex = categoryText.IndexOf(" (");
                if (propCountIndex > 0)
                {
                    categoryText = categoryText.Substring(0, propCountIndex).Trim();
                }

                // Build corrected JSON object
                var correctedData = new JObject
                {
                    ["DocumentType"] = selectedDocType,
                    ["ComponentCategory"] = categoryText,
                    ["ManufacturerInfo"] = manufacturerTextBox.Text.Trim(),
                    ["CorrectionNote"] = correctionNoteTextBox.Text.Trim(),
                    ["CorrectionTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["OriginalCategory"] = _originalJson["ComponentCategory"]?.Value<string>() ?? "",
                    ["WasManuallyCorrected"] = true
                };

                // Add part number for resistor categories
                // Applies to: "Resistor", "Resistor Constant", "Resistor Network", etc.
                bool isResistor = categoryText.IndexOf("Resistor", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isResistor && !string.IsNullOrWhiteSpace(partNumberTextBox.Text))
                {
                    correctedData["PartNumber"] = partNumberTextBox.Text.Trim();
                }

                // Preserve original ComponentName (not editable)
                if (_originalJson["ComponentName"] != null)
                {
                    correctedData["ComponentName"] = _originalJson["ComponentName"].Value<string>();
                }

                // Set results and close
                CorrectedJson = correctedData;
                WasCorrected = true;

                // Log correction for debugging
                Console.WriteLine("\n=== KORREKTUR ANGEWENDET ===");
                Console.WriteLine($"Original: {_originalJson["ComponentCategory"]}");
                Console.WriteLine($"Korrigiert: {correctedData["ComponentCategory"]}");
                Console.WriteLine($"Notiz: {correctedData["CorrectionNote"]}");

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Fehler beim Anwenden der Korrektur: {ex.Message}",
                    "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles Cancel button click.
        /// Closes dialog without applying changes.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            WasCorrected = false;
            DialogResult = false;
            Close();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Updates part number panel visibility based on component category.
        /// Part number field is only shown for resistor-related categories.
        /// 
        /// Applies to categories containing "Resistor":
        /// - Resistor
        /// - Resistor Constant
        /// - Resistor Network
        /// - etc.
        /// </summary>
        /// <param name="category">Component category name</param>
        private void UpdatePartNumberVisibility(string category)
        {
            bool isResistor = !string.IsNullOrWhiteSpace(category) &&
                             category.IndexOf("Resistor", StringComparison.OrdinalIgnoreCase) >= 0;

            partNumberPanel.Visibility = isResistor ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion
    }
}
