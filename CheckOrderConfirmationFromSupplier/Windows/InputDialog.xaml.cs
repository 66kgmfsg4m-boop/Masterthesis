using System.Windows;
using System.Windows.Input;

namespace CheckOrderConfirmationFromSupplier.Windows
{
    public partial class InputDialog : Window
    {
        public string InputText { get; private set; }

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();

            Title = title;
            promptTextBlock.Text = prompt;
            inputTextBox.Text = defaultValue;
            inputTextBox.SelectAll();
            inputTextBox.Focus();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            InputText = inputTextBox.Text;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OkButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                CancelButton_Click(sender, e);
            }
        }
    }
}
