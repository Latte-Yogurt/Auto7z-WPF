using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Auto7z_WPF
{
    /// <summary>
    /// 支持属性变更通知的基础类。
    /// </summary>
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 通用命令实现，支持 CanExecute 变更通知（用于任务执行期间的界面禁用）。
    /// </summary>
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(Cast(parameter));
        }

        public void Execute(object parameter)
        {
            _execute(Cast(parameter));
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);

            // 通知 CommandManager 重新查询可执行状态，
            // 否则输入绑定缓存的旧状态不会更新
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// 宽容地把命令参数转换为 T：参数为 null 时返回默认值，类型不匹配时也返回默认值，
        /// 避免 (T)parameter 在参数缺失时抛出 InvalidCastException。
        /// </summary>
        private static T Cast(object parameter)
        {
            if (parameter is T typed) return typed;
            if (parameter == null) return default(T);
            return default(T);
        }
    }
}
