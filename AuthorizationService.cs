using System;
using System.Collections.Generic;
using System.Windows;

namespace CheckOrderConfirmationFromSupplier.Services
{
    /// <summary>
    /// Service for managing user authorization and access control.
    /// Loads authorized user list from Azure Blob Storage and validates current Windows user.
    /// Prevents unauthorized access to the application.
    /// </summary>
    public class AuthorizationService
    {
        private Dictionary<string, string> _authorizedUsers;
        private readonly RemoteConfigService _remoteConfigService;

        /// <summary>
        /// Initializes the authorization service.
        /// Creates a RemoteConfigService instance with unique client ID for Azure communication.
        /// Immediately loads authorized users from Azure Blob Storage.
        /// </summary>
        public AuthorizationService()
        {
            // Initialize RemoteConfigService with unique client ID for Azure Blob Storage access
            _remoteConfigService = new RemoteConfigService(Guid.NewGuid().ToString());
            LoadAuthorizedUsers();
        }

        /// <summary>
        /// Loads the list of authorized users from Azure Blob Storage.
        /// The authorized-users configuration file contains username-to-welcome-message mappings.
        /// Throws exception if loading fails (critical - application cannot start without authorization data).
        /// </summary>
        private void LoadAuthorizedUsers()
        {
            try
            {
                Console.WriteLine("================== AUTHORIZATION ==================");
                Console.WriteLine("Loading user permissions from Azure Blob Storage...");

                // Load authorized users from Azure Blob Storage (authorized-users configuration)
                _authorizedUsers = _remoteConfigService.LoadConfig("authorized-users");

                Console.WriteLine($"User permissions loaded from Azure: {(_authorizedUsers != null)}");
                Console.WriteLine($"Number of authorized users: {(_authorizedUsers != null ? _authorizedUsers.Count : 0)}");

                // Validate that user data was loaded successfully
                if (_authorizedUsers == null || _authorizedUsers.Count == 0)
                {
                    throw new Exception("No user permissions loaded from Azure - application will terminate");
                }

                Console.WriteLine("? Authorization data loaded successfully");
                Console.WriteLine("====================================================");
            }
            catch (Exception e)
            {
                Console.WriteLine($"? ERROR loading from Azure: {e.Message}");
                Console.WriteLine("====================================================");
                throw new Exception($"Critical error: User permissions could not be loaded from Azure: {e.Message}", e);
            }
        }

        /// <summary>
        /// Checks if the current Windows user is authorized to use the application.
        /// Compares the current Windows username against the authorized users list from Azure.
        /// Shows welcome dialog if authorized, or access denied dialog if not authorized.
        /// </summary>
        /// <returns>True if user is authorized, false otherwise</returns>
        public bool CheckUserAuthorization()
        {
            string currentUser = GetCurrentUser();
            Console.WriteLine($"Checking authorization for user: {currentUser}");

            // Ensure authorization data is available
            if (_authorizedUsers == null)
            {
                Console.WriteLine("? ERROR: No user permissions available!");
                ShowErrorDialog("No user permissions available!", "Authorization Error");
                return false;
            }

            // Check if current user exists in authorized users dictionary
            bool isAuthorized = _authorizedUsers.ContainsKey(currentUser);

            if (isAuthorized)
            {
                // User is authorized - get welcome message and display confirmation
                string welcomeMessage = _authorizedUsers[currentUser];
                Console.WriteLine($"? User authorized: {currentUser} - {welcomeMessage}");

                // Show welcome dialog with personalized message
                MessageBox.Show(
                    $"Welcome {currentUser}!\n{welcomeMessage}",
                    "Authorization Successful",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return true;
            }
            else
            {
                // User is NOT authorized - deny access
                Console.WriteLine($"? User NOT authorized: {currentUser}");

                ShowErrorDialog(
                    $"You are not authorized to use this application.\n" +
                    $"Your username: {currentUser}\n" +
                    $"Please contact your administrator.",
                    "Access Denied");
                return false;
            }
        }

        /// <summary>
        /// Gets the current Windows username from environment variables.
        /// </summary>
        /// <returns>Current Windows username (e.g., "john.doe")</returns>
        private string GetCurrentUser()
        {
            return Environment.UserName;
        }

        /// <summary>
        /// Displays an error dialog with custom message and title.
        /// Used for authorization failures and critical errors.
        /// </summary>
        /// <param name="message">Error message to display</param>
        /// <param name="title">Dialog title</param>
        private void ShowErrorDialog(string message, string title)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
