using System;
using System.Windows.Input;

namespace Auto7z_WPF
{
    /// <summary>
    /// 无参命令实现。
    /// </summary>
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

            // 通知 CommandManager 重新查询可执行状态。
            // MouseBinding 等输入绑定通过 CommandManager 缓存命令状态，
            // 只触发自身事件的话缓存不会更新，命令在新状态下依旧不执行。
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
