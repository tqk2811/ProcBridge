using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Tests.Fakes;

namespace ProcBridge.Agent.Wpf.Tests;

[TestClass]
public sealed class PairingViewModelTests
{
    private static string Correct(PairingViewModel viewModel) => FakePairingPrompt.Fingerprint.Split('-')[viewModel.HiddenGroupNumber - 1];

    private static string Wrong(PairingViewModel viewModel) => viewModel.Choices.First(x => x != Correct(viewModel));

    [TestMethod]
    public void Challenge_HidesOneGroup_AndOffersItAmongDistinctChoices()
    {
        var viewModel = new PairingViewModel(new FakePairingPrompt(), new Random(1));

        Assert.HasCount(PairingViewModel.ChoiceCount, viewModel.Choices);
        Assert.HasCount(PairingViewModel.ChoiceCount, viewModel.Choices.Distinct());
        Assert.Contains(Correct(viewModel), viewModel.Choices);
        Assert.DoesNotContain(Correct(viewModel), viewModel.MaskedFingerprint);
        Assert.AreEqual(PairingViewModel.HiddenGroup, viewModel.MaskedFingerprint.Split('-')[viewModel.HiddenGroupNumber - 1]);
        Assert.IsFalse(viewModel.AcceptCommand.CanExecute(null));
    }

    [TestMethod]
    public void Accept_BeforeFingerprintConfirmed_DoesNotAccept()
    {
        var prompt = new FakePairingPrompt();
        var viewModel = new PairingViewModel(prompt, new Random(2));

        Assert.IsFalse(viewModel.Accept());
        viewModel.AcceptCommand.Execute(null);

        Assert.AreEqual(0, prompt.AcceptCalls);
        Assert.IsFalse(viewModel.IsDecided);
    }

    [TestMethod]
    public void WrongChoice_KeepsAcceptDisabled_AndChangesTheHiddenGroup()
    {
        var prompt = new FakePairingPrompt();
        var viewModel = new PairingViewModel(prompt, new Random(3));
        int hidden = viewModel.HiddenGroupNumber;

        Assert.IsFalse(viewModel.Choose(Wrong(viewModel)));

        Assert.IsFalse(viewModel.IsFingerprintConfirmed);
        Assert.IsFalse(viewModel.AcceptCommand.CanExecute(null));
        Assert.IsFalse(viewModel.Accept());
        Assert.AreEqual(0, prompt.AcceptCalls);
        Assert.AreEqual(0, prompt.RejectCalls);
        Assert.IsNotNull(viewModel.ErrorText);
        Assert.AreNotEqual(hidden, viewModel.HiddenGroupNumber);
    }

    [TestMethod]
    public void TooManyWrongChoices_RejectsTheRequest()
    {
        var prompt = new FakePairingPrompt();
        var viewModel = new PairingViewModel(prompt, new Random(4));
        int closes = 0;
        viewModel.CloseRequested += () => closes++;

        for (int i = 0; i < PairingViewModel.MaxWrongAttempts; i++)
            viewModel.Choose(Wrong(viewModel));

        Assert.AreEqual(1, prompt.RejectCalls);
        Assert.AreEqual(0, prompt.AcceptCalls);
        Assert.IsTrue(viewModel.IsDecided);
        Assert.AreEqual(1, closes);
        // A right pick after the rejection changes nothing.
        Assert.IsFalse(viewModel.Choose(Correct(viewModel)));
        Assert.IsFalse(viewModel.Accept());
    }

    [TestMethod]
    public void RightChoice_EnablesAccept_ThenAcceptCallsThePromptOnce()
    {
        var prompt = new FakePairingPrompt();
        var viewModel = new PairingViewModel(prompt, new Random(5));

        Assert.IsTrue(viewModel.Choose(Correct(viewModel)));

        Assert.IsTrue(viewModel.AcceptCommand.CanExecute(null));
        Assert.AreEqual(FakePairingPrompt.Fingerprint, viewModel.MaskedFingerprint);
        Assert.IsTrue(viewModel.Accept());
        Assert.IsFalse(viewModel.Accept());
        Assert.AreEqual(1, prompt.AcceptCalls);
        Assert.AreEqual(0, prompt.RejectCalls);
    }

    [TestMethod]
    public void RightChoiceAfterAWrongOne_StillWorks()
    {
        var prompt = new FakePairingPrompt();
        var viewModel = new PairingViewModel(prompt, new Random(6));

        viewModel.Choose(Wrong(viewModel));
        Assert.IsTrue(viewModel.Choose(Correct(viewModel)));
        Assert.IsTrue(viewModel.Accept());

        Assert.AreEqual(1, prompt.AcceptCalls);
    }

    [TestMethod]
    public void Reject_CallsThePrompt_AndBlocksAccept()
    {
        var prompt = new FakePairingPrompt();
        var viewModel = new PairingViewModel(prompt, new Random(7));
        viewModel.Choose(Correct(viewModel));

        viewModel.RejectCommand.Execute(null);

        Assert.AreEqual(1, prompt.RejectCalls);
        Assert.IsFalse(viewModel.Accept());
        Assert.AreEqual(0, prompt.AcceptCalls);
    }

    [TestMethod]
    public async Task RequestEndingByItself_AsksTheDialogToClose()
    {
        var prompt = new FakePairingPrompt();
        var viewModel = new PairingViewModel(prompt, new Random(8));
        var closed = new TaskCompletionSource();
        viewModel.CloseRequested += () => closed.TrySetResult();

        prompt.CompletionSource.SetResult(false);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
