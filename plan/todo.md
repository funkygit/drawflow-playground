# Task: Implement Advanced Node Execution

## Completed Tasks
- [x] Update `WorkflowEntities.cs` models
    - [x] Add `ParameterRequirement`, `NodeOutput`
    - [x] Structure `NodeConfiguration` for `Lifecycle` vs `ExecutionFlow`
- [x] Create `Utilities\ParameterParser.cs`
- [x] Update `Utilities\DynamicExecutor.cs`
    - [x] Update to work with `MethodDefinition`
    - [x] Support creating/returning instances
- [x] Update `WorkflowExecutionService.cs`
    - [x] Implement `ParameterParser` usage
    - [x] Implement `LongRunning` logic (OnStart, Store Instance)
    - [x] Implement `Transient` logic (ExecutionFlow sequence)
    - [x] Implement `StopNode` logic (OnStop)

## Task: Node Input Selection & Validation

- [x] Update `WorkflowEntities.cs` models
    - [x] Add `NodeInput` class (SourceNodeId, SourceOutputName)
    - [x] Update `NodeParameter` to hold `NodeInput` value (when Source is "NodeOutput") -> Note: Handled via `QueueItem` logic.
    - [x] Add `public NodeInput Input { get; set; }` to `ExecutionQueueItem`
- [x] Implement Validation Logic
    - [x] Create `WorkflowValidator` service/utility
    - [x] Validate type compatibility between Source Output and Target Input
- [x] Update `WorkflowExecutionService.cs`
    - [x] Resolve inputs from `ExecutionQueueItem.Input`
    - [x] Map resolved `NodeInput` values to parameters with `Source="NodeOutput"`

## Remaining/Future
- [ ] Frontend: Ensure UI sends `NodeInput` structure when queueing logic is triggered.
- [ ] Testing: Comprehensive end-to-end test of a LongRunning node passing data to a Transient node.
