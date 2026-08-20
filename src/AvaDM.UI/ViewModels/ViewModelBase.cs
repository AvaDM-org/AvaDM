using CommunityToolkit.Mvvm.ComponentModel;

namespace AvaDM.UI.ViewModels;

/// <summary>Common base for every page/row view model in the app. Just <see cref="ObservableObject"/>
/// under a project-local name so call sites read as "AvaDM view model", not "toolkit object" -
/// per the plan, CommunityToolkit.Mvvm ([ObservableProperty]/[RelayCommand]) is used throughout;
/// ReactiveUI is not used anywhere in this project.</summary>
public abstract class ViewModelBase : ObservableObject;
