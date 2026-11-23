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

        /// <summary>
        /// <see cref="ICommand"/> 要求的事件。
        ///
        /// 本类**从不主动触发**它（原先有个 RaiseCanExecuteChanged 就要用它，但那个方法
        /// 全仓零调用，已作为死代码删除）。保留声明是因为接口要求；加 add/remove 空实现
        /// 而不是 <c>event EventHandler X;</c>，是为了消除编译器 CS0067「事件从不使用」警告 ——
        /// 那条警告会让「编译零警告」这条基线失效。
        ///
        /// 如果将来真的需要「命令可用性变化时刷新按钮」，正确的做法是重新引入一个
        /// 有调用方的触发点，并且同时调 CommandManager.InvalidateRequerySuggested()，
        /// 而不是只挂一个没人触发的事件。
        /// </summary>
        public event EventHandler CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(Cast(parameter));
        }

        public void Execute(object parameter)
        {
            _execute(Cast(parameter));
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
