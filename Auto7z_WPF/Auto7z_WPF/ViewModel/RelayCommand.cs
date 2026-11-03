using System;
using System.Windows.Input;

namespace Auto7z_WPF
{
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter) => _execute();

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);

            // WPF 的按钮同时挂在 CommandManager 的全局重查询上，
            // 不主动失效的话，可用性变了按钮也不会跟着变灰。
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
