use std::collections::{BTreeMap, VecDeque};
use std::fs;
use std::io::{self, BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Mutex, mpsc};
use std::time::{Duration, Instant};

use crossterm::event::{self, Event, KeyCode, KeyEventKind};
use crossterm::execute;
use crossterm::terminal::{
    EnterAlternateScreen, LeaveAlternateScreen, disable_raw_mode, enable_raw_mode,
};
use ratatui::layout::{Constraint, Direction, Layout};
use ratatui::style::{Color, Modifier, Style};
use ratatui::text::{Line, Span};
use ratatui::widgets::{Block, Borders, List, ListItem, Paragraph, Wrap};
use ratatui::{Frame, Terminal};
use serde::Deserialize;

#[derive(Debug, Clone, Deserialize)]
struct LauncherConfig {
    tasks: Vec<LaunchTask>,
}

#[derive(Debug, Clone, Deserialize)]
struct LaunchTask {
    name: String,
    description: String,
    program: String,
    #[serde(default)]
    args: Vec<String>,
    cwd: Option<String>,
    #[serde(default)]
    env: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TaskStatus {
    Idle,
    Running,
    Success,
    Failed,
}

#[derive(Debug)]
enum ProcEvent {
    Output(String),
    Exit(i32),
}

struct RunningProcess {
    child: Arc<Mutex<Child>>,
    task_index: usize,
    started_at: Instant,
}

struct App {
    tasks: Vec<LaunchTask>,
    statuses: Vec<TaskStatus>,
    selected: usize,
    log_lines: VecDeque<String>,
    running: Option<RunningProcess>,
    tx: mpsc::Sender<ProcEvent>,
    rx: mpsc::Receiver<ProcEvent>,
    root_dir: PathBuf,
}

fn main() -> io::Result<()> {
    let root_dir = std::env::current_dir()?;
    let tasks = load_tasks(&root_dir)?;
    if tasks.is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "No tasks configured in launcher.tasks.json",
        ));
    }
    if std::env::args().any(|a| a == "--check") {
        println!("launcher tasks loaded: {}", tasks.len());
        return Ok(());
    }

    enable_raw_mode()?;
    execute!(io::stdout(), EnterAlternateScreen)?;
    let backend = ratatui::backend::CrosstermBackend::new(io::stdout());
    let mut terminal = Terminal::new(backend)?;

    let (tx, rx) = mpsc::channel();
    let mut app = App {
        statuses: vec![TaskStatus::Idle; tasks.len()],
        tasks,
        selected: 0,
        log_lines: VecDeque::new(),
        running: None,
        tx,
        rx,
        root_dir,
    };

    app.log("Launcher ready. Select a task and press Enter.");

    let result = run_app(&mut terminal, &mut app);

    if let Some(r) = &app.running {
        if let Ok(mut child) = r.child.lock() {
            let _ = child.kill();
        }
    }

    disable_raw_mode()?;
    execute!(io::stdout(), LeaveAlternateScreen)?;
    result
}

fn run_app(
    terminal: &mut Terminal<ratatui::backend::CrosstermBackend<io::Stdout>>,
    app: &mut App,
) -> io::Result<()> {
    loop {
        while let Ok(ev) = app.rx.try_recv() {
            match ev {
                ProcEvent::Output(line) => app.log(&line),
                ProcEvent::Exit(code) => {
                    if let Some(r) = app.running.take() {
                        let status = if code == 0 {
                            TaskStatus::Success
                        } else {
                            TaskStatus::Failed
                        };
                        app.statuses[r.task_index] = status;
                        let elapsed = r.started_at.elapsed().as_secs_f32();
                        app.log(&format!(
                            "[exit] task '{}' finished with code {} in {:.1}s",
                            app.tasks[r.task_index].name, code, elapsed
                        ));
                    }
                }
            }
        }

        terminal.draw(|f| draw_ui(f, app))?;

        if !event::poll(Duration::from_millis(120))? {
            continue;
        }

        if let Event::Key(key) = event::read()? {
            if key.kind != KeyEventKind::Press {
                continue;
            }
            match key.code {
                KeyCode::Char('q') => return Ok(()),
                KeyCode::Up => {
                    if app.selected > 0 {
                        app.selected -= 1;
                    }
                }
                KeyCode::Down => {
                    if app.selected + 1 < app.tasks.len() {
                        app.selected += 1;
                    }
                }
                KeyCode::Enter => {
                    if app.running.is_some() {
                        app.log("A task is already running. Press 'k' to stop it first.");
                    } else if let Err(e) = start_selected_task(app) {
                        app.log(&format!("Failed to start task: {}", e));
                    }
                }
                KeyCode::Char('k') => {
                    stop_running_task(app);
                }
                KeyCode::Char('c') => {
                    app.log_lines.clear();
                }
                _ => {}
            }
        }
    }
}

fn draw_ui(frame: &mut Frame, app: &App) {
    let root = Layout::default()
        .direction(Direction::Vertical)
        .constraints([Constraint::Min(5), Constraint::Length(3)])
        .split(frame.area());

    let body = Layout::default()
        .direction(Direction::Horizontal)
        .constraints([Constraint::Percentage(36), Constraint::Percentage(64)])
        .split(root[0]);

    let items: Vec<ListItem> = app
        .tasks
        .iter()
        .enumerate()
        .map(|(i, t)| {
            let marker = match app.statuses[i] {
                TaskStatus::Idle => " ",
                TaskStatus::Running => "▶",
                TaskStatus::Success => "✓",
                TaskStatus::Failed => "✗",
            };
            let color = match app.statuses[i] {
                TaskStatus::Idle => Color::Gray,
                TaskStatus::Running => Color::Yellow,
                TaskStatus::Success => Color::Green,
                TaskStatus::Failed => Color::Red,
            };
            ListItem::new(Line::from(vec![
                Span::styled(format!("[{}] ", marker), Style::default().fg(color)),
                Span::raw(t.name.clone()),
            ]))
        })
        .collect();

    let list = List::new(items)
        .highlight_style(
            Style::default()
                .fg(Color::Cyan)
                .add_modifier(Modifier::BOLD),
        )
        .highlight_symbol(">> ")
        .block(Block::default().borders(Borders::ALL).title("Tasks"));

    let mut state = ratatui::widgets::ListState::default();
    state.select(Some(app.selected));
    frame.render_stateful_widget(list, body[0], &mut state);

    let details_chunks = Layout::default()
        .direction(Direction::Vertical)
        .constraints([Constraint::Length(6), Constraint::Min(4)])
        .split(body[1]);

    let selected_task = &app.tasks[app.selected];
    let running_text = match &app.running {
        Some(r) => format!(
            "running: {} ({:.1}s)",
            app.tasks[r.task_index].name,
            r.started_at.elapsed().as_secs_f32()
        ),
        None => "running: none".to_string(),
    };
    let detail_text = format!(
        "{}\nprogram: {}\nargs: {}\n{}\nroot: {}",
        selected_task.description,
        selected_task.program,
        if selected_task.args.is_empty() {
            "(none)".to_string()
        } else {
            selected_task.args.join(" ")
        },
        running_text,
        app.root_dir.display()
    );

    let details = Paragraph::new(detail_text)
        .wrap(Wrap { trim: true })
        .block(Block::default().borders(Borders::ALL).title("Details"));
    frame.render_widget(details, details_chunks[0]);

    let visible_logs: Vec<Line> = app
        .log_lines
        .iter()
        .map(|line| Line::from(Span::raw(line.clone())))
        .collect();
    let logs = Paragraph::new(visible_logs)
        .wrap(Wrap { trim: false })
        .block(Block::default().borders(Borders::ALL).title("Output"));
    frame.render_widget(logs, details_chunks[1]);

    let footer = Paragraph::new(
        "Up/Down: select  Enter: launch  k: stop  c: clear logs  q: quit",
    )
    .block(Block::default().borders(Borders::ALL).title("Keys"));
    frame.render_widget(footer, root[1]);
}

fn start_selected_task(app: &mut App) -> io::Result<()> {
    let idx = app.selected;
    let task = app.tasks[idx].clone();
    let run_dir = resolve_task_cwd(&app.root_dir, task.cwd.as_deref());
    let mut cmd = Command::new(&task.program);
    cmd.args(&task.args)
        .current_dir(run_dir)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for (k, v) in &task.env {
        cmd.env(k, v);
    }

    let mut child = cmd.spawn()?;
    app.log(&format!(
        "[start] {} -> {} {}",
        task.name,
        task.program,
        task.args.join(" ")
    ));
    app.statuses[idx] = TaskStatus::Running;

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();
    let child_arc = Arc::new(Mutex::new(child));

    if let Some(out) = stdout {
        let tx = app.tx.clone();
        std::thread::spawn(move || {
            let rdr = BufReader::new(out);
            for line in rdr.lines().map_while(Result::ok) {
                let _ = tx.send(ProcEvent::Output(line));
            }
        });
    }
    if let Some(err) = stderr {
        let tx = app.tx.clone();
        std::thread::spawn(move || {
            let rdr = BufReader::new(err);
            for line in rdr.lines().map_while(Result::ok) {
                let _ = tx.send(ProcEvent::Output(format!("[stderr] {}", line)));
            }
        });
    }

    {
        let tx = app.tx.clone();
        let child_for_wait = Arc::clone(&child_arc);
        std::thread::spawn(move || {
            let code = child_for_wait
                .lock()
                .ok()
                .and_then(|mut c| c.wait().ok())
                .and_then(|s| s.code())
                .unwrap_or(-1);
            let _ = tx.send(ProcEvent::Exit(code));
        });
    }

    app.running = Some(RunningProcess {
        child: child_arc,
        task_index: idx,
        started_at: Instant::now(),
    });
    Ok(())
}

fn stop_running_task(app: &mut App) {
    let Some(running) = &app.running else {
        app.log("No running task to stop.");
        return;
    };

    let name = app.tasks[running.task_index].name.clone();
    let child_handle = Arc::clone(&running.child);
    match child_handle.lock() {
        Ok(mut child) => {
            if child.kill().is_ok() {
                app.log(&format!("[kill] sent stop signal to '{}'", name));
            } else {
                app.log(&format!("[kill] failed to stop '{}'", name));
            }
        }
        Err(_) => app.log("[kill] process lock failed"),
    }
}

fn resolve_task_cwd(root: &Path, task_cwd: Option<&str>) -> PathBuf {
    match task_cwd {
        None => root.to_path_buf(),
        Some(p) => {
            let rel = PathBuf::from(p);
            if rel.is_absolute() {
                rel
            } else {
                root.join(rel)
            }
        }
    }
}

fn load_tasks(root_dir: &Path) -> io::Result<Vec<LaunchTask>> {
    let config_path = root_dir.join("tools").join("launcher-tui").join("launcher.tasks.json");
    let txt = fs::read_to_string(&config_path).map_err(|e| {
        io::Error::new(
            io::ErrorKind::NotFound,
            format!("cannot read {}: {}", config_path.display(), e),
        )
    })?;
    let cfg: LauncherConfig = serde_json::from_str(&txt).map_err(|e| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            format!("invalid json in {}: {}", config_path.display(), e),
        )
    })?;
    Ok(cfg.tasks)
}

impl App {
    fn log(&mut self, msg: &str) {
        if self.log_lines.len() >= 700 {
            let _ = self.log_lines.pop_front();
        }
        self.log_lines.push_back(msg.to_string());
    }
}
