"""Typed bounded batch requests; Unity remains the authority for target/property validation."""
from typing import Annotated, Literal
from pydantic import BaseModel, ConfigDict, Field, JsonValue


class StrictRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")


class BatchLimits(StrictRequest):
    maxNodes: int = Field(default=512, ge=1, le=4096)
    maxTargets: int = Field(default=128, ge=1, le=512)
    maxDepth: int = Field(default=32, ge=1, le=64)
    maxCollection: int = Field(default=32, ge=1, le=128)
    maxValues: int = Field(default=4096, ge=1, le=16384)
    maxResponseBytes: int = Field(default=262144, ge=8192, le=1048576)
    maxMilliseconds: int = Field(default=100, ge=1, le=1000)


PropertyPaths = Annotated[list[Annotated[str, Field(min_length=1, max_length=256)]], Field(min_length=1, max_length=32)]


class Operation(StrictRequest):
    id: str = Field(min_length=1, max_length=64)
    target: str = Field(min_length=1, max_length=64, description="Opaque reference from acquire_editor_references or a batch read; never an instance ID.")
    component: str | None = Field(default=None, min_length=1, max_length=256)
    componentIndex: int | None = Field(default=None, ge=0)


class InspectOperation(Operation):
    op: Literal["inspect"]
    properties: PropertyPaths | None = None


class SetFieldsOperation(Operation):
    op: Literal["set_fields"]
    fields: dict[str, JsonValue] = Field(min_length=1, max_length=32)
    expected: dict[str, JsonValue] | None = Field(default=None, max_length=32)


BatchOperation = Annotated[InspectOperation | SetFieldsOperation, Field(discriminator="op")]
