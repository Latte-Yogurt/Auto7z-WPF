using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Auto7z_WPF
{
    public class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            // 值不变就不发通知，免得绑定层白重算一遍；
            // 需要「相同值也刷新」的场景必须显式传名字并自己调 OnPropertyChanged。
            if (Equals(field, value)) return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

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

            // 只发自己的事件不够：WPF 的按钮还挂在 CommandManager 的全局重查询上，
            // 不主动失效时，命令可用性变了按钮也不会跟着变灰。
            CommandManager.InvalidateRequerySuggested();
        }

        private static T Cast(object parameter)
        {
            // 参数类型对不上时给 default 而不抛异常：XAML 的 CommandParameter
            // 是字符串，泛型参数传不进来属于常态，抛异常会让整次点击崩掉。
            if (parameter is T typed) return typed;
            if (parameter == null) return default(T);
            return default(T);
        }
    }
}
