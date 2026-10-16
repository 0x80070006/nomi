using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Nomi
{
    static class Ui
    {
        // XAML files are embedded as resources and loaded at run time (built with csc only, no XAML compiler).
        public static T Load<T>(string name) where T : class
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nomi." + name))
            {
                if (s == null) throw new InvalidOperationException("Ressource manquante : " + name);
                return (T)XamlReader.Load(s);
            }
        }

        public static T Find<T>(FrameworkElement root, string name) where T : class
        {
            var found = root.FindName(name) as T;
            if (found == null) throw new InvalidOperationException("Élément introuvable : " + name);
            return found;
        }

        public static void InstallAppResources(Application app)
        {
            app.Resources["ZeroToVisible"] = new ZeroToVisibleConverter();
            app.Resources.MergedDictionaries.Add(Load<ResourceDictionary>("Styles.xaml"));
            // Defaults, so nothing is unresolved before a window applies its theme.
            Theme.Apply(app.Resources, new Settings());
        }
    }

    class ZeroToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is int && (int)value == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    // One row of the Outlook suggestions list.
    class Suggestion : INotifyPropertyChanged
    {
        public MailItem Mail;
        string text = "", status = "En attente";
        public bool DraftSaved;
        public bool Failed;

        public string Title { get { return Mail.Title; } }
        public string Preview
        {
            get
            {
                string t = string.IsNullOrEmpty(text) ? (Mail.Received == DateTime.MinValue ? "" : "Reçu " + Mail.Received.ToString("dddd HH:mm", CultureInfo.CurrentCulture)) : text;
                return t.Replace("\r", " ").Replace("\n", " ");
            }
        }
        public string Text { get { return text; } set { text = value ?? ""; Changed("Text"); Changed("Preview"); } }
        public string Status { get { return status; } set { status = value; Changed("Status"); } }

        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }
    }
}
