using System.Windows.Input;

namespace VisionCanvas.MvvmSample.Commands;

/// <summary>
/// 버튼과 단축키가 함께 사용하는 실행 단위다. 실행 중 중복 클릭을 막고 실패를 화면에 알린다.
/// ICommand는 WPF가 '지금 실행 가능한가?'와 '실행해라'를 요청할 때 사용하는 약속이다.
/// </summary>
// 클래스 이름 뒤 괄호는 생성자가 받을 값이다(주 생성자 문법).
// Func<Task>: 실행할 비동기 작업, Func<bool>: 실행 가능 여부, Action<Exception>: 오류 처리 함수.
public sealed class EditorCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> failed) : ICommand
{
    private bool _running;
    // 이 알림을 받은 버튼은 CanExecute를 다시 물어보고 활성/비활성 상태를 바꾼다.
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && canExecute();

    // ICommand의 Execute는 반환형이 void로 정해져 있다. 여기서만 async void로 연결한다.
    // 실제 작업은 Task를 반환하는 ExecuteAsync에 두어 테스트에서도 완료를 기다릴 수 있게 한다.
    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }
        _running = true;
        try
        {
            Refresh();
            // await는 작업 완료를 기다린다. 아직 끝나지 않았다면 UI 스레드를 붙잡지 않는다.
            // async/await 자체가 작업을 별도 스레드로 옮기는 것은 아니다.
            await execute();
        }
        catch (Exception error)
        {
            failed(error);
        }
        finally
        {
            // 성공·실패 어느 경우든 '실행 중' 상태를 풀어 다음 클릭이 영구히 막히지 않게 한다.
            _running = false;
            Refresh();
        }
    }

    // ?.는 연결된 알림 수신자가 있을 때만 호출한다는 뜻이다.
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
