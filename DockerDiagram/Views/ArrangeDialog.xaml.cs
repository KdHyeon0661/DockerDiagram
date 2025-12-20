using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ArrangeDialog : Window
    {
        public int Columns { get; private set; } = 3;

        public ArrangeDialog()
        {
            InitializeComponent();
            txtCols.Focus();
            txtCols.SelectAll();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(txtCols.Text, out int c) && c > 0)
            {
                Columns = c;
                DialogResult = true;
            }
            else
            {
                MessageBox.Show("열(Column) 개수는 1 이상의 숫자여야 합니다.");
            }
        }
    }
}