using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace DEEPAi.ServiceDirectory.Tray.Commands
{
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Predicate<object> _canExecute;
        private readonly Action<Exception> _onError;
        private bool _isExecuting;

        public AsyncCommand(
            Func<Task> execute,
            Func<bool> canExecute,
            Action<Exception> onError)
            : this(
                parameter => execute(),
                canExecute == null
                    ? (Predicate<object>)null
                    : parameter => canExecute(),
                onError)
        {
            if (execute == null)
            {
                throw new ArgumentNullException(nameof(execute));
            }
        }

        public AsyncCommand(
            Func<object, Task> execute,
            Predicate<object> canExecute,
            Action<Exception> onError)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        }

        public event EventHandler CanExecuteChanged;

        public bool IsExecuting => _isExecuting;

        public bool CanExecute(object parameter)
        {
            return !_isExecuting
                && (_canExecute == null || _canExecute(parameter));
        }

        public async void Execute(object parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isExecuting = true;
            RaiseCanExecuteChanged();
            try
            {
                await _execute(parameter);
            }
            catch (Exception exception)
            {
                _onError(exception);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
