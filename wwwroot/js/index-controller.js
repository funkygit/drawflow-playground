var id = document.getElementById("drawflow");
const editor = new Drawflow(id);
editor.reroute = true;
editor.start();

// SignalR Setup
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/workflowHub")
    .build();

let currentWorkflowId = null;
let currentExecutor = null;

connection.on("NodeStatusChanged", (executionId, nodeId, status) => {
    // Filter for current execution
    if (currentExecutor && currentExecutor.executionId === executionId) {
        log(`Node ${nodeId}: ${status}`);
        updateNodeVisuals(nodeId, status);
        
        if (status === "Completed") {
            currentExecutor.handleNodeCompletion(nodeId);
        }
    }
});

connection.on("ExecutionPaused", (id) => log(`Execution ${id} PAUSED`));
connection.on("ExecutionResumed", (id) => log(`Execution ${id} RESUMED`));
connection.on("ExecutionAborted", (id) => log(`Execution ${id} ABORTED`));

connection.start().then(() => {
    log("Connected to SignalR");
}).catch(err => log("SignalR Error: " + err));

function updateNodeVisuals(nodeId, status) {
    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (nodeEl) {
        let badge = nodeEl.querySelector('.node-status');
        if (!badge) {
            badge = document.createElement('div');
            badge.className = 'node-status';
            nodeEl.appendChild(badge);
        }
        badge.className = `node-status status-${status.toLowerCase()}`;
    }
}

// Drag & Drop
function drag(ev) {
    ev.dataTransfer.setData("node", ev.target.getAttribute('data-node'));
}

id.addEventListener('drop', (ev) => {
    ev.preventDefault();
    const nodeType = ev.dataTransfer.getData("node");
    addNodeToDrawflow(nodeType, ev.clientX, ev.clientY);
});

id.addEventListener('dragover', (ev) => {
    ev.preventDefault();
});

function addNodeToDrawflow(type, posx, posy) {
    if(editor.editor_mode === 'fixed') return false;
    posx = posx * ( editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)) - (editor.precanvas.getBoundingClientRect().x * ( editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)));
    posy = posy * ( editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)) - (editor.precanvas.getBoundingClientRect().y * ( editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)));

    let inputs = 1; 
    let outputs = 1;
    if(type === 'start') { inputs = 1; outputs = 1; }
    if(type === 'end') { inputs = 1; outputs = 0; }
    if(type === 'loop') { inputs = 1; outputs = 1; }
    // Delay is standard 1 in 1 out

    const html = `<div>${type.toUpperCase()}</div>`;
    const data = { name: type }; 
    editor.addNode(type, inputs, outputs, posx, posy, type, data, html);
}

// API Calls
async function saveWorkflow() {
    const data = editor.export();
    const workflow = {
        Id: currentWorkflowId || undefined,
        Name: "My Workflow " + new Date().toISOString(),
        JsonData: JSON.stringify(data)
    };

    const res = await fetch('/Home/SaveWorkflow', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(workflow)
    });
    const result = await res.json();
    currentWorkflowId = result.id;
    log(`Workflow Saved: ${currentWorkflowId}`);
}

async function runWorkflow() {
    if (!currentWorkflowId) {
        await saveWorkflow();
    }
    
    // Reset Visuals
    document.querySelectorAll('.node-status').forEach(el => el.remove());

    const executionId = await connection.invoke("StartWorkflow", currentWorkflowId, null);
    log(`Started Execution: ${executionId}`);
    
    await connection.invoke("JoinWorkflowGroup", executionId);

    // Init Executor
    currentExecutor = new WorkflowExecutor(editor, connection, executionId, currentWorkflowId, async (newExecId) => {
        // Handle Loop: Switch context to new Execution ID
        currentExecutor = new WorkflowExecutor(editor, connection, newExecId, currentWorkflowId, currentExecutor.onChainExecution);
        // Join new group
        connection.invoke("JoinWorkflowGroup", newExecId);
        // Clear visuals for new run
        document.querySelectorAll('.node-status').forEach(el => el.remove());
        log(`Switched to Loop Execution: ${newExecId}`);
        await currentExecutor.start();
    });
    await currentExecutor.start();
}

function log(msg) {
    const logDiv = document.getElementById('log');
    if (!logDiv) return;
    const p = document.createElement('p');
    p.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
    logDiv.prepend(p);
}
