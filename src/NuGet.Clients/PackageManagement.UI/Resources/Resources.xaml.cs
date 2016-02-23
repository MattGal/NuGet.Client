using System.Windows;

namespace NuGet.PackageManagement.UI
{
    public partial class SharedResources : ResourceDictionary
    {
        public SharedResources()
        {
            InitializeComponent();
        }

        private void PackageIconImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var image = sender as Image;
            image.Source = Images.DefaultPackageIcon;
        }
    }
}