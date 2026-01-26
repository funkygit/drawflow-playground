var id = document.getElementById("drawflow");
const editor = new Drawflow(id);
editor.reroute = true;
editor.start();

// SignalR Setup
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/workflowHub")
    .build();

let currentWorkflowId = null;
let currentExecutionId = null;

connection.on("NodeStatusChanged", (nodeId, status) => {
    log(`Node ${nodeId}: ${status}`);
    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (nodeEl) {
        // Simple visual update (e.g., border color)
        // Drawflow nodes have id "node-X" inside the execution container
        // We might need to add a custom badge
        let badge = nodeEl.querySelector('.node-status');
        if (!badge) {
            badge = document.createElement('div');
            badge.className = 'node-status';
            nodeEl.appendChild(badge);
        }
        badge.className = `node-status status-${status.toLowerCase()}`;
    }
});

connection.start().then(() => {
    log("Connected to SignalR");
}).catch(err => log("SignalR Error: " + err));

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
    if(type === 'start') { inputs = 0; outputs = 1; }
    if(type === 'end') { inputs = 1; outputs = 0; }

    const html = `<div>${type.toUpperCase()}</div>`;
    // data can be anything
    const data = { name: type }; 
    editor.addNode(type, inputs, outputs, posx, posy, type, data, html);
}

// API Calls
async function saveWorkflow() {
    const data = editor.export();
    const workflow = {
        Id: currentWorkflowId || undefined,
        Name: "My Workflow",
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
    
    // Join group
    await connection.invoke("JoinWorkflowGroup", currentWorkflowId); // Wait, we group by ExecutionID usually? 
    // In current implementation: Hub joins workflowId group.
    // Service sends to executionId group. This is a mismatch in my code above.
    // Service sends to `executionId.ToString()`.
    // Hub `StartWorkflow` returns executionId.
    // Client should join executionId group? Or service sends to workflowId group?
    // Let's fix this in JS: Join the ExecutionID group once we get it.
    
    const executionId = await connection.invoke("StartWorkflow", currentWorkflowId);
    currentExecutionId = executionId;
    log(`Started Execution: ${executionId}`);
    
    // Join the execution group to receive updates
    await connection.invoke("JoinWorkflowGroup", executionId);
}

function log(msg) {
    const logDiv = document.getElementById('log');
    const p = document.createElement('p');
    p.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
    logDiv.prepend(p);
}
