using Microsoft.UI.Xaml.Controls;
using MyTodo.App.Services;
using MyTodo.Core.Models;

namespace MyTodo.App.Views;

/// <summary>
/// The right-click menu for a task, shared by the Report list and the Board's list view so the two
/// cannot drift into offering different actions for the same task. It is also the mouse path to the
/// stage actions that the list view otherwise exposes only through swipe.
/// </summary>
public static class TaskMenu
{
    public static MenuFlyout Build(AppServices s, TodoTask t, Action<TodoTask> toggle, Action<TodoTask> delete, Action<TodoTask>? edit = null)
    {
        var menu = new MenuFlyout();

        if (edit != null)
        {
            var open = new MenuFlyoutItem { Text = "Edit", Icon = new SymbolIcon(Symbol.Edit) };
            open.Click += (_, _) => edit(t);
            menu.Items.Add(open);
        }

        var complete = new MenuFlyoutItem { Text = t.IsCompleted ? "Reopen" : "Complete" };
        complete.Click += (_, _) => toggle(t);
        menu.Items.Add(complete);

        if (t.IsArchived)
        {
            var restore = new MenuFlyoutItem { Text = "Restore from archive" };
            restore.Click += (_, _) => s.Store.ReopenTask(t);
            menu.Items.Add(restore);
        }

        // The same transitions the stage buttons and swipe actions run.
        if (!t.IsCompleted)
        {
            var stage = new MenuFlyoutItem { Text = t.IsInProgress ? "Hold" : t.IsOnHold ? "Resume" : "Start" };
            if (t.IsInProgress) stage.Click += (_, _) => s.Store.HoldTask(t);
            else stage.Click += (_, _) => s.Store.StartTask(t);
            menu.Items.Add(stage);
        }

        var moveTo = new MenuFlyoutSubItem { Text = "Move to" };
        foreach (var l in s.Store.GetLists().Where(l => l.Id != t.ListId))
        {
            var target = l;
            var item = new MenuFlyoutItem { Text = l.Name };
            item.Click += (_, _) =>
            {
                t.ListId = target.Id;
                s.Store.SaveTask(t);
                foreach (var sub in s.Store.GetSubtasks(t.Id)) { sub.ListId = target.Id; s.Store.SaveTask(sub); }
            };
            moveTo.Items.Add(item);
        }
        if (moveTo.Items.Count > 0) menu.Items.Add(moveTo);

        menu.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += (_, _) => delete(t);
        menu.Items.Add(remove);
        return menu;
    }
}
