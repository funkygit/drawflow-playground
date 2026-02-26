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
let nodeMetaList = [];

async function fetchNodeMeta() {
    try {
        const res = await fetch('/Home/GetNodeMeta');
        nodeMetaList = await res.json();
        renderAvailableNodes();
    } catch (err) {
        log("Error fetching node meta: " + err);
    }
}

function renderAvailableNodes() {
    const container = document.getElementById('available-nodes');
    container.innerHTML = '';

    // Group by NodeType and sort by NodeTypeOrder
    const grouped = nodeMetaList.reduce((acc, meta) => {
        const key = meta.nodeType || "Other";
        if (!acc[key]) acc[key] = { order: meta.nodeTypeOrder, list: [] };
        acc[key].list.push(meta);
        return acc;
    }, {});

    const sortedCategories = Object.keys(grouped).sort((a, b) => grouped[a].order - grouped[b].order);

    sortedCategories.forEach(cat => {
        const header = document.createElement('h6');
        header.className = 'mt-2 text-primary';
        header.innerText = cat;
        container.appendChild(header);

        grouped[cat].list.forEach(meta => {
            const item = document.createElement('div');
            item.className = 'list-group-item list-group-item-action py-1 px-2 small drag-drawflow';
            item.draggable = true;
            item.innerText = meta.displayName;
            item.dataset.node = 'module';
            item.dataset.nodeKey = meta.nodeKey;
            item.ondragstart = drag;
            container.appendChild(item);
        });
    });
}

fetchNodeMeta();

connection.on("NodeStatusChanged", (executionId, nodeId, status, triggeredOutput) => {
    // Filter for current execution
    if (currentExecutor && currentExecutor.executionId === executionId) {
        log(`Node ${nodeId}: ${status}${triggeredOutput ? ' [' + triggeredOutput + ']' : ''}`);
        updateNodeVisuals(nodeId, status);

        if (status === "Completed") {
            currentExecutor.handleNodeCompletion(nodeId, triggeredOutput);
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
    if (ev.target.getAttribute('data-node-key')) {
        ev.dataTransfer.setData("nodeKey", ev.target.getAttribute('data-node-key'));
    }
}

id.addEventListener('drop', (ev) => {
    ev.preventDefault();
    const nodeType = ev.dataTransfer.getData("node");
    const nodeKey = ev.dataTransfer.getData("nodeKey");
    addNodeToDrawflow(nodeType, ev.clientX, ev.clientY, nodeKey);
});

id.addEventListener('dragover', (ev) => {
    ev.preventDefault();
});

// ============================================================
// Wrappers for shared functions (pass editor/nodeMetaList globals)
// ============================================================

function _findPrevNodes(nodeId, requiredType) {
    return findConnectedPreviousNodes(editor, nodeMetaList, nodeId, requiredType);
}

function _renderNodeParams(nodeId, data, meta) {
    renderNodeParams(nodeId, data, meta, _findPrevNodes);
}

function _updateNodeDataFromContent(nodeId) {
    updateNodeDataFromContent(editor, nodeId);
}

// ============================================================
// Add Node to Drawflow
// ============================================================

function addNodeToDrawflow(type, posx, posy, nodeKey = null) {
    if (editor.editor_mode === 'fixed') return false;
    posx = posx * (editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)) - (editor.precanvas.getBoundingClientRect().x * (editor.precanvas.clientWidth / (editor.precanvas.clientWidth * editor.zoom)));
    posy = posy * (editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)) - (editor.precanvas.getBoundingClientRect().y * (editor.precanvas.clientHeight / (editor.precanvas.clientHeight * editor.zoom)));

    let inputs = 1;
    let outputs = 1;
    let html = `<div>${type.toUpperCase()}</div>`;
    let data = { name: type };

    if (nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === nodeKey);
        if (meta) {
            data = {
                nodeKey: nodeKey,
                displayName: meta.displayName,
                nodeType: meta.nodeType,
                selectedVariant: meta.variants && meta.variants.length > 0 ? meta.variants[0].value : null,
                parameters: {} // To be filled by config
            };

            // Build the rich in-node HTML (nodeId not yet known, will be patched)
            html = buildNodeContentHtml(meta, data, '?');

            // Derive input/output counts from variant metadata
            const defaultVariant = meta.variants && meta.variants.length > 0 ? meta.variants[0] : null;

            // Control nodes (Conditional, Iterator) use outputs.length for multiple execution ports
            // Processing nodes always get 1 output port (data outputs are fields within a single result)
            if (meta.nodeTypeKey === 'control') {
                const outputCount = defaultVariant && defaultVariant.outputs ? defaultVariant.outputs.length : 0;
                outputs = outputCount;
            }
            if (meta.nodeTypeKey === 'input') { inputs = 0; }
            if (meta.nodeTypeKey === 'output' || meta.nodeKey === 'end') { outputs = 0; }
        }
    } else {
        if (type === 'start') { inputs = 0; outputs = 1; }
        if (type === 'end') { inputs = 1; outputs = 0; }
        if (type === 'loop') { inputs = 1; outputs = 1; }
        if (type === 'conditional') { inputs = 1; outputs = 2; }
        if (type === 'iterator') { inputs = 1; outputs = 2; }
    }

    const newNodeId = editor.addNode(type, inputs, outputs, posx, posy, type, data, html);

    // After creation, patch the node ID into the content and render params
    if (nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === nodeKey);
        if (meta) {
            // Patch the node ID badge
            const nodeEl = document.getElementById(`node-${newNodeId}`);
            if (nodeEl) {
                const idBadge = nodeEl.querySelector('.node-id-value');
                if (idBadge) idBadge.textContent = newNodeId;
            }

            // Render parameters table
            _renderNodeParams(newNodeId, data, meta);

            // Add output port labels for control nodes
            labelOutputPorts(newNodeId, meta);

            // Attach event listeners
            attachNodeEventListeners(newNodeId, data, meta);
        }
    }
}

/**
 * Finds all downstream nodes connected to a given node's outputs
 * and re-renders their parameter forms so NODE_OUTPUT selects are refreshed.
 */
function refreshDownstreamNodes(sourceNodeId) {
    const exportData = editor.export();
    if (!exportData.drawflow.Home || !exportData.drawflow.Home.data) return;

    const nodes = exportData.drawflow.Home.data;
    const sourceNode = nodes[sourceNodeId];
    if (!sourceNode || !sourceNode.outputs) return;

    // Collect all node IDs connected to this node's outputs
    const downstreamIds = new Set();
    for (const outputKey in sourceNode.outputs) {
        const connections = sourceNode.outputs[outputKey].connections;
        if (connections) {
            connections.forEach(conn => downstreamIds.add(conn.node));
        }
    }

    // Re-render params for each downstream node
    downstreamIds.forEach(downstreamId => {
        const node = editor.getNodeFromId(downstreamId);
        if (node && node.data && node.data.nodeKey) {
            const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
            if (meta) {
                _updateNodeDataFromContent(downstreamId); // Preserve existing values
                _renderNodeParams(downstreamId, node.data, meta);
                attachNodeEventListeners(downstreamId, node.data, meta);
            }
        }
    });
}

/**
 * Attaches interactive event listeners to the in-node controls.
 */
function attachNodeEventListeners(nodeId, data, meta) {
    const nodeEl = document.getElementById(`node-${nodeId}`);
    if (!nodeEl) return;

    // Variant selector change
    const variantSelect = nodeEl.querySelector('.node-variant-select');
    if (variantSelect) {
        variantSelect.addEventListener('change', (e) => {
            e.stopPropagation();
            data.selectedVariant = e.target.value;

            // Toggle parameter row visibility
            const paramsContainer = nodeEl.querySelector('.node-params-container');
            if (paramsContainer) {
                const renderer = new DynamicFormRenderer(meta, () => []);
                renderer.toggleVariantVisibility(paramsContainer, data.selectedVariant);
            }

            // Refresh downstream nodes so their NODE_OUTPUT selects reflect the new outputs
            refreshDownstreamNodes(nodeId);

            log(`Node ${nodeId}: Variant changed to ${data.selectedVariant}`);
        });
    }

    // Update button
    const updateBtn = nodeEl.querySelector('.node-update-btn');
    if (updateBtn) {
        updateBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            _updateNodeDataFromContent(nodeId);
        });
    }

    // Prevent click propagation on inputs/selects so Drawflow doesn't interfere
    nodeEl.querySelectorAll('input, select, button').forEach(el => {
        el.addEventListener('mousedown', (e) => e.stopPropagation());
        el.addEventListener('click', (e) => e.stopPropagation());
    });
}

// ============================================================
// API Calls
// ============================================================

async function saveWorkflow() {
    // Extract clean data (auto-refreshes all params, strips HTML)
    const data = extractCleanData(editor);

    const nameInput = document.getElementById('workflow-name');
    const name = (nameInput && nameInput.value.trim()) || "My Workflow " + new Date().toISOString();

    const workflow = {
        Id: currentWorkflowId || undefined,
        Name: name,
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

// ============================================================
// Load Workflow
// ============================================================

async function showLoadDialog() {
    try {
        const res = await fetch('/Home/GetWorkflows');
        const workflows = await res.json();

        if (!workflows || workflows.length === 0) {
            alert('No saved workflows found.');
            return;
        }

        // Build a simple selection list
        const names = workflows.map((wf, i) => `${i + 1}. ${wf.name || 'Untitled'} (${wf.id})`);
        const choice = prompt('Select a workflow to load:\n\n' + names.join('\n') + '\n\nEnter number:');

        if (choice) {
            const index = parseInt(choice, 10) - 1;
            if (index >= 0 && index < workflows.length) {
                await loadWorkflow(workflows[index].id, workflows[index].name);
            }
        }
    } catch (err) {
        log("Error loading workflows: " + err);
    }
}

async function loadWorkflow(workflowId, workflowName) {
    try {
        const res = await fetch(`/Home/GetWorkflow?id=${workflowId}`);
        const workflow = await res.json();

        if (!workflow || !workflow.jsonData) {
            log("Failed to load workflow data.");
            return;
        }

        const cleanData = JSON.parse(workflow.jsonData);

        // Rehydrate — rebuilds HTML from meta, imports, renders params
        rehydrateWorkflow(editor, nodeMetaList, cleanData, {
            findPrevNodesFn: _findPrevNodes,
            onNodeReady: (nodeId, data, meta) => {
                attachNodeEventListeners(nodeId, data, meta);
            }
        });

        currentWorkflowId = workflowId;

        // Update the name input
        const nameInput = document.getElementById('workflow-name');
        if (nameInput && workflowName) {
            nameInput.value = workflowName;
        }

        log(`Loaded Workflow: ${workflowName || workflowId}`);
    } catch (err) {
        log("Error loading workflow: " + err);
        console.error(err);
    }
}

// ============================================================
// Run Workflow
// ============================================================

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
    currentExecutor = new WorkflowExecutor(editor, connection, executionId, currentWorkflowId, nodeMetaList, async (newExecId) => {
        // Handle Loop: Switch context to new Execution ID
        currentExecutor = new WorkflowExecutor(editor, connection, newExecId, currentWorkflowId, nodeMetaList, currentExecutor.onChainExecution);
        // Join new group
        connection.invoke("JoinWorkflowGroup", newExecId);
        // Clear visuals for new run
        document.querySelectorAll('.node-status').forEach(el => el.remove());
        log(`Switched to Loop Execution: ${newExecId}`);
        await currentExecutor.start();
    });
    await currentExecutor.start();
}

// ============================================================
// Connection Events — Refresh NODE_OUTPUT Selects
// ============================================================

editor.on('connectionCreated', (connection) => {
    // connection = { output_id, input_id, output_class, input_class }
    _updateNodeDataFromContent(connection.input_id); // Save existing input values before re-rendering

    // Re-render params for the target node (to refresh NODE_OUTPUT selects)
    const node = editor.getNodeFromId(connection.input_id);
    if (node && node.data && node.data.nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
        if (meta) {
            _renderNodeParams(connection.input_id, node.data, meta);
            attachNodeEventListeners(connection.input_id, node.data, meta);
        }
    }
});

editor.on('connectionRemoved', (connection) => {
    // connection = { output_id, input_id, output_class, input_class }
    _updateNodeDataFromContent(connection.input_id); // Save existing input values before re-rendering

    // Re-render params for the target node
    const node = editor.getNodeFromId(connection.input_id);
    if (node && node.data && node.data.nodeKey) {
        const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
        if (meta) {
            _renderNodeParams(connection.input_id, node.data, meta);
            attachNodeEventListeners(connection.input_id, node.data, meta);
        }
    }
});

// ============================================================
// Logging
// ============================================================

function log(msg) {
    const logDiv = document.getElementById('log');
    if (!logDiv) return;
    const p = document.createElement('p');
    p.textContent = `[${new Date().toLocaleTimeString()}] ${msg}`;
    logDiv.prepend(p);
}

// ============================================================
// Right-Click Context Menu
// ============================================================

let contextNodeId = null;
const contextMenu = document.getElementById('node-context-menu');

// Show context menu on right-click on a Drawflow node
document.getElementById('drawflow').addEventListener('contextmenu', (e) => {
    // Walk up from the target to find the .drawflow-node parent
    const nodeEl = e.target.closest('.drawflow-node');
    if (!nodeEl) {
        contextMenu.style.display = 'none';
        return;
    }

    e.preventDefault();

    // Extract node ID from the element id (format: "node-{id}")
    const idMatch = nodeEl.id.match(/^node-(\d+)$/);
    if (!idMatch) return;

    contextNodeId = idMatch[1];

    // Position and show context menu
    contextMenu.style.left = e.clientX + 'px';
    contextMenu.style.top = e.clientY + 'px';
    contextMenu.style.display = 'block';
});

// Hide context menu on click elsewhere
document.addEventListener('click', () => {
    contextMenu.style.display = 'none';
});

// ============================================================
// Node Test Popup
// ============================================================

function testSelectedNode() {
    contextMenu.style.display = 'none';
    if (!contextNodeId) return;

    const node = editor.getNodeFromId(contextNodeId);
    if (!node || !node.data || !node.data.nodeKey) {
        alert('This node cannot be tested (no configuration).');
        return;
    }

    const meta = nodeMetaList.find(m => m.nodeKey === node.data.nodeKey);
    if (!meta) {
        alert('Node metadata not found.');
        return;
    }

    // Find the selected variant
    const selectedVariant = node.data.selectedVariant || (meta.variants && meta.variants.length > 0 ? meta.variants[0].value : null);
    const variant = meta.variants.find(v => v.value === selectedVariant);
    if (!variant) {
        alert('No variant found for this node.');
        return;
    }

    // Collect all params from the variant
    const params = variant.parameters || [];

    // Set modal title
    document.getElementById('test-modal-title').textContent = `🧪 Test: ${meta.displayName}${meta.variants.length > 1 ? ' (' + variant.label + ')' : ''}`;

    // Build param form
    let html = '<table>';
    if (params.length === 0) {
        html += '<tr><td colspan="2" style="text-align:center;color:#888;">No parameters</td></tr>';
    }

    params.forEach(param => {
        const currentValue = (node.data.parameters && node.data.parameters[param.name]) || param.value || '';
        const sourceClass = param.source === 'NODE_OUTPUT' ? 'badge-node-output'
                          : param.source === 'USER_INPUT'  ? 'badge-user-input'
                          : 'badge-constant';
        const sourceLabel = param.source === 'NODE_OUTPUT' ? 'NODE_OUTPUT'
                          : param.source === 'USER_INPUT'  ? 'INPUT'
                          : 'CONST';

        html += `<tr>`;
        html += `<td>${param.displayName || param.name} <span class="param-source-badge ${sourceClass}">${sourceLabel}</span></td>`;
        html += `<td>`;

        if (param.source === 'Constant') {
            html += `<input type="text" name="${param.name}" value="${currentValue}" readonly />`;
        } else if (param.allowedValues && param.allowedValues.length > 0) {
            html += `<select name="${param.name}">`;
            param.allowedValues.forEach(v => {
                const selected = v === currentValue ? ' selected' : '';
                html += `<option value="${v}"${selected}>${v}</option>`;
            });
            html += `</select>`;
        } else if (param.source === 'NODE_OUTPUT') {
            // For NODE_OUTPUT, show empty input for test data (ignore the "nodeId.outputName" reference)
            html += `<input type="text" name="${param.name}" value="" placeholder="Enter test value for ${param.displayName || param.name}" />`;
        } else {
            html += `<input type="text" name="${param.name}" value="${currentValue}" />`;
        }

        html += `</td></tr>`;
    });

    html += '</table>';

    // Store context in the modal for executeNodeTest
    const modalBody = document.getElementById('test-modal-body');
    modalBody.innerHTML = html;
    modalBody.dataset.nodeKey = node.data.nodeKey;
    modalBody.dataset.variantValue = selectedVariant;

    document.getElementById('test-modal-status').textContent = '';
    document.getElementById('test-run-btn').disabled = false;

    // Show modal
    document.getElementById('test-modal-overlay').style.display = 'flex';
}

function closeTestModal() {
    document.getElementById('test-modal-overlay').style.display = 'none';
}

// Close on overlay click (outside modal)
document.getElementById('test-modal-overlay').addEventListener('click', (e) => {
    if (e.target === e.currentTarget) closeTestModal();
});

async function executeNodeTest() {
    const modalBody = document.getElementById('test-modal-body');
    const nodeKey = modalBody.dataset.nodeKey;
    const variantValue = modalBody.dataset.variantValue;
    const statusEl = document.getElementById('test-modal-status');
    const runBtn = document.getElementById('test-run-btn');

    // Collect params from form
    const params = {};
    modalBody.querySelectorAll('input:not([readonly]), select').forEach(el => {
        if (el.name) params[el.name] = el.value;
    });

    statusEl.textContent = '⏳ Executing...';
    runBtn.disabled = true;

    // Remove old result box
    const oldResult = modalBody.querySelector('.test-result-box');
    if (oldResult) oldResult.remove();

    try {
        const res = await fetch('/Home/TestNode', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                nodeKey: nodeKey,
                variantValue: variantValue,
                parameters: params
            })
        });

        const result = await res.json();

        const resultBox = document.createElement('div');
        resultBox.className = 'test-result-box';

        if (result.success) {
            resultBox.classList.add('test-result-success');
            let text = `✓ Output: ${result.output}\n`;
            if (result.steps && result.steps.length > 0) {
                text += '\nExecution Steps:\n';
                result.steps.forEach((step, i) => {
                    text += `  ${i + 1}. ${step.method}() → ${JSON.stringify(step.result)}\n`;
                });
            }
            resultBox.textContent = text;
            statusEl.textContent = '✓ Test completed successfully';
        } else {
            resultBox.classList.add('test-result-error');
            resultBox.textContent = `✗ Error: ${result.error}${result.innerError ? '\n  Inner: ' + result.innerError : ''}`;
            statusEl.textContent = '✗ Test failed';
        }

        modalBody.appendChild(resultBox);
        log(`Test ${nodeKey}: ${result.success ? 'SUCCESS' : 'FAILED'} — ${result.output || result.error}`);
    } catch (err) {
        statusEl.textContent = '✗ Request failed';
        const resultBox = document.createElement('div');
        resultBox.className = 'test-result-box test-result-error';
        resultBox.textContent = `✗ Network error: ${err.message}`;
        modalBody.appendChild(resultBox);
        log(`Test ${nodeKey} failed: ${err.message}`);
    }

    runBtn.disabled = false;
}
