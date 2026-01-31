using System.Windows;

namespace DockerDiagram.Views
{
    public partial class ArrangeDialog : Window // 그루핑이 된 컨테이너들의 정렬 대화상자
    {
        public int Columns { get; private set; } = 3; // 기본값 3열

        public ArrangeDialog()
        {
            InitializeComponent();
            txtCols.Focus();
            txtCols.SelectAll();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) // 확인 버튼
        {
            if (int.TryParse(txtCols.Text, out int c) && c > 0) // 열 개수가 1 이상의 숫자인지 확인
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