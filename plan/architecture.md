# Project Context & Recent Changes

## 1. Core Objective
The project is a **Workflow Execution Engine** based on Drawflow (frontend) and .NET 8 (backend). The recent focus has been on implementing **Advanced Node Execution** capabilities, specifically supporting:
- **Long-Running Nodes**: Stateful nodes (e.g., TCP Servers) with a lifecycle (`OnStart`, `OnEvent`, `OnStop`).
- **Transient Nodes**: Stateless nodes that execute a sequence of methods and complete.
- **Dynamic Parameter Binding**: Linking outputs of one node to the inputs of another with type validation.

## 2. Key Components & Architecture

### Data Models (`WorkflowEntities.cs`)
- **NodeConfiguration**: The blueprint for a node. Defines `ExecutionMode` (LongRunning/Transient), `Lifecycle` methods, `ExecutionFlow` sequences, and `Outputs`.
- **NodeInput**: Represents a connection from a previous node's output (`SourceNodeId`, `SourceOutputName`).
- **ExecutionQueueItem**: Represents a task to be processed. recently updated to include a specific `NodeInput` causing the execution.
- **ExecutionResult**: Stores the output of a node execution. Now supports storing complex objects (serialized as JSON) to support multiple named outputs.

### Services
- **WorkflowExecutionService**:
  - Manages the execution loop (BackgroundService).
  - Orchestrates node execution based on `ExecutionMode`.
  - **LongRunning**: Instantiates the node, calls `OnStart`, and tracks the instance in `_activeNodeInstances`.
  - **Transient**: Instantiates, runs the `ExecutionFlow` sequence, and disposes.
  - **Resolution**: Resolves `NodeInput` values by querying `ExecutionResults` from the database.
- **DynamicExecutor**:
  - Handles the reflection magic.
  - Loads assemblies, creates instances, and invokes methods defined in `MethodDefinition`.
  - Uses `ParameterParser` to map inputs to method arguments.
- **ParameterParser**:
  - Validates and converts dictionary inputs into strongly-typed method arguments.
  - Handles `IsRequired` logic (static or conditional checks).
- **WorkflowValidator**:
  - Validates the workflow structure before execution.
  - Checks if linked nodes exist and if Output Types match Input Types.

## 3. Recent Changes & Refactoring

### A. Lifecycle & Execution Modes
We moved away from a simple "one-method-per-node" model to a structured definition:
- **Old**: `MethodName` string.
- **New**: 
  - `Lifecycle` object (OnStart, OnEvent, OnStop) for LongRunning.
  - `ExecutionFlow` list (Sequence of MethodDefinitions) for Transient.

### B. Input/Output Linking
- **NodeInput**: Introduced to explicitly model data flow between nodes.
- **Queueing**: `QueueNode` now accepts a `NodeInput`. This allows the execution service to know *exactly* which data triggered the node.
- **Resolution Strategy**:
  - **Previous**: Parsed the huge Workflow JSON at runtime to find links. (Inefficient/Brittle)
  - **Current**: The `ExecutionQueueItem` carries the `NodeInput` info. The service uses this to look up the specific `ExecutionResult` from the DB.

### C. Parameter Binding
- **Logic**: In `ProcessNode`, after resolving the `NodeInput` value:
  - We iterate through the node's configured parameters (`OnStart` or `ExecutionFlow`).
  - Any parameter with `Source == "NodeOutput"` is automatically assigned the resolved value.
