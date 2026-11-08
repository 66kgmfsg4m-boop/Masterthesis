using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CheckOrderConfirmationFromSupplier.Windows
{
    public partial class ResistorParameterDialog : Window
    {
        public JObject ResistorProperties { get; private set; }
        public bool WasConfirmed { get; private set; }

        private bool _isAutoDecoded = false;
        private string _partNumber;

        public ResistorParameterDialog(string partNumber)
        {
            InitializeComponent();

            _partNumber = partNumber;
            partNumberDisplay.Text = partNumber;
            WasConfirmed = false;
            _isAutoDecoded = false;

            this.Title = "Widerstands-Parameter manuell eingeben";
            autoDecodedInfoPanel.Visibility = Visibility.Collapsed;
            manualInputWarning.Visibility = Visibility.Visible;
        }

        public ResistorParameterDialog(string partNumber, JObject decodedProperties)
        {
            InitializeComponent();

            _partNumber = partNumber;
            partNumberDisplay.Text = partNumber;
            WasConfirmed = false;
            _isAutoDecoded = true;

            this.Title = "Widerstands-Parameter bestätigen";
            autoDecodedInfoPanel.Visibility = Visibility.Visible;
            manualInputWarning.Visibility = Visibility.Collapsed;

            PreFillDecodedValues(decodedProperties);
        }

        private void PreFillDecodedValues(JObject decoded)
        {
            try
            {
                Console.WriteLine("\nFÜLLE DIALOG MIT DEKODIERTEN WERTEN VOR:");

                double resistanceOhm = decoded["ResistaOhm"]?.Value<double>() ?? 0;
                if (resistanceOhm > 0)
                {
                    if (resistanceOhm >= 1000000)
                    {
                        resistanceValueTextBox.Text = (resistanceOhm / 1000000).ToString(CultureInfo.InvariantCulture);
                        resistanceUnitComboBox.SelectedIndex = 2;
                        Console.WriteLine($"   Widerstand: {resistanceOhm / 1000000} MOhm");
                    }
                    else if (resistanceOhm >= 1000)
                    {
                        resistanceValueTextBox.Text = (resistanceOhm / 1000).ToString(CultureInfo.InvariantCulture);
                        resistanceUnitComboBox.SelectedIndex = 1;
                        Console.WriteLine($"   Widerstand: {resistanceOhm / 1000} kOhm");
                    }
                    else
                    {
                        resistanceValueTextBox.Text = resistanceOhm.ToString(CultureInfo.InvariantCulture);
                        resistanceUnitComboBox.SelectedIndex = 0;
                        Console.WriteLine($"   Widerstand: {resistanceOhm} Ohm");
                    }
                }

                double tolerancePct = decoded["TolerancePct"]?.Value<double>() ?? 1.0;
                string toleranceStr = $"±{tolerancePct}%";
                SelectComboBoxItem(toleranceComboBox, toleranceStr);
                Console.WriteLine($"   Toleranz: {toleranceStr}");

                double powerW = decoded["PowerW"]?.Value<double>() ?? 0.1;
                if (powerW >= 1.0)
                {
                    powerValueTextBox.Text = powerW.ToString(CultureInfo.InvariantCulture);
                    powerUnitComboBox.SelectedIndex = 0;
                    Console.WriteLine($"   Leistung: {powerW} W");
                }
                else
                {
                    powerValueTextBox.Text = (powerW * 1000).ToString(CultureInfo.InvariantCulture);
                    powerUnitComboBox.SelectedIndex = 1;
                    Console.WriteLine($"   Leistung: {powerW * 1000} mW");
                }

                string packageType = decoded["PackageType"]?.Value<string>();
                if (!string.IsNullOrEmpty(packageType))
                {
                    packageComboBox.Text = packageType;
                    Console.WriteLine($"   Package: {packageType}");
                }

                double tempCoeffPPM = decoded["TempCoeffPPM"]?.Value<double>() ?? 0;
                if (tempCoeffPPM > 0)
                {
                    string tempCoeffStr = $"±{tempCoeffPPM} ppm/°C";
                    tempCoeffComboBox.Text = tempCoeffStr;
                    Console.WriteLine($"   Temp.Koeff.: {tempCoeffStr}");
                }

                string decodingSource = decoded["DecodingSource"]?.Value<string>() ?? "Unknown";
                string confidence = decoded["DecodingConfidence"]?.Value<string>() ?? "Medium";

                Console.WriteLine($"   Quelle: {decodingSource}");
                Console.WriteLine($"   Confidence: {confidence}");

                if (autoDecodedSourceLabel != null)
                {
                    autoDecodedSourceLabel.Text = $"Quelle: {decodingSource} (Confidence: {confidence})";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fehler beim Vorausfüllen der Werte: {ex.Message}");
            }
        }

        private void SelectComboBoxItem(ComboBox comboBox, string text)
        {
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Content.ToString() == text)
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            if (comboBox.IsEditable)
            {
                comboBox.Text = text;
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ValidateRequiredFields())
                {
                    MessageBox.Show(
                        "Bitte geben Sie mindestens den Widerstandswert ein.\n\n" +
                        "Der Widerstandswert ist das einzige Pflichtfeld.\n" +
                        "Alle anderen Felder sind optional.",
                        "Widerstandswert erforderlich",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }

                ResistorProperties = BuildResistorProperties();

                if (_isAutoDecoded)
                {
                    ResistorProperties["DataSource"] = "Part Number Decoding (User Confirmed)";
                    Console.WriteLine("Automatisch dekodierte Werte vom User bestätigt");
                }
                else
                {
                    ResistorProperties["DataSource"] = "Manual User Input";
                    Console.WriteLine("Manuell eingegebene Werte vom User bestätigt");
                }

                WasConfirmed = true;
                this.DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Fehler beim Verarbeiten der Eingaben:\n\n{ex.Message}",
                    "Fehler",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private JObject BuildResistorProperties()
        {
            var properties = new JObject();
            properties["InputTimestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            properties["PartNumber"] = _partNumber;
            properties["ComponentType"] = "Resistor";
            properties["Function"] = "Resistor";

            // Widerstandswert (ERFORDERLICH)
            string resistanceValue = resistanceValueTextBox.Text.Trim().Replace(",", ".");
            string resistanceUnit = (resistanceUnitComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "Ohm";
            double resistanceOhm = ConvertToOhm(resistanceValue, resistanceUnit);
            properties["ResistaOhm"] = resistanceOhm.ToString(CultureInfo.InvariantCulture);

            // Toleranz (OPTIONAL)
            string toleranceStr = GetComboBoxValue(toleranceComboBox);
            if (toleranceStr != "nicht vorhanden" && toleranceStr.Contains("%"))
            {
                string toleranceValue = toleranceStr.Replace("±", "").Replace("%", "").Trim();
                properties["TolerancePct"] = toleranceValue;
            }
            else
            {
                properties["TolerancePct"] = "nicht vorhanden";
            }

            // Leistung (OPTIONAL)
            if (!string.IsNullOrWhiteSpace(powerValueTextBox.Text))
            {
                string powerValue = powerValueTextBox.Text.Trim().Replace(",", ".");
                string powerUnit = (powerUnitComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "W";
                double powerW = ConvertToWatt(powerValue, powerUnit);
                properties["PowerW"] = powerW.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                properties["PowerW"] = "nicht vorhanden";
            }

            // Package (OPTIONAL)
            properties["PackageType"] = GetComboBoxValue(packageComboBox);

            // Temperaturkoeffizient (OPTIONAL)
            string tempCoeffValue = GetComboBoxValue(tempCoeffComboBox);
            if (tempCoeffValue != "nicht vorhanden" && tempCoeffValue.Contains("ppm"))
            {
                string tempCoeffNumeric = tempCoeffValue.Replace("±", "").Replace("ppm/°C", "").Trim();
                properties["TempCoeffPPM"] = tempCoeffNumeric;
            }
            else
            {
                properties["TempCoeffPPM"] = "nicht vorhanden";
            }

            // Max Operating Temperature (OPTIONAL)
            string maxTempValue = GetComboBoxValue(maxTempComboBox);
            if (!string.IsNullOrEmpty(maxTempValue) && maxTempValue != "nicht vorhanden")
            {
                properties["MaxOperatingTemp"] = maxTempValue;
            }
            else
            {
                properties["MaxOperatingTemp"] = "nicht vorhanden";
            }

            // Max Overload Voltage (OPTIONAL)
            string voltageValue = maxVoltageTextBox.Text?.Trim();
            if (!string.IsNullOrEmpty(voltageValue))
            {
                string voltageUnit = (voltageUnitComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "V";
                properties["MaxOverloadVoltage"] = $"{voltageValue} {voltageUnit}";
            }
            else
            {
                properties["MaxOverloadVoltage"] = "nicht vorhanden";
            }

            return properties;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            WasConfirmed = false;
            this.DialogResult = false;
            Close();
        }

        private bool ValidateRequiredFields()
        {
            if (string.IsNullOrWhiteSpace(resistanceValueTextBox.Text))
                return false;

            if (!double.TryParse(resistanceValueTextBox.Text.Trim().Replace(",", "."),
                NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                return false;

            return true;
        }

        private string GetComboBoxValue(ComboBox comboBox)
        {
            if (comboBox.IsEditable && !string.IsNullOrWhiteSpace(comboBox.Text))
            {
                return comboBox.Text.Trim();
            }
            else if (comboBox.SelectedItem != null)
            {
                return (comboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "";
            }
            return "nicht vorhanden";
        }

        private double ConvertToOhm(string value, string unit)
        {
            double numericValue = double.Parse(value.Replace(",", "."), CultureInfo.InvariantCulture);

            switch (unit)
            {
                case "kOhm":
                case "k?":
                    return numericValue * 1000;
                case "MOhm":
                case "M?":
                    return numericValue * 1000000;
                case "Ohm":
                case "?":
                default:
                    return numericValue;
            }
        }

        private double ConvertToWatt(string value, string unit)
        {
            double numericValue = double.Parse(value.Replace(",", "."), CultureInfo.InvariantCulture);

            switch (unit)
            {
                case "kW":
                    return numericValue * 1000;
                case "MW":
                    return numericValue * 1000000;
                case "mW":
                    return numericValue / 1000;
                case "µW":
                    return numericValue / 1000000;
                case "W":
                default:
                    return numericValue;
            }
        }
    }
}
