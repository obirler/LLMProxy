# ProjectFeature Specification

## Overview
A Mixture of Agents feature will be added.

## Requirements
- New routing strategy named "Mixture of Agents" (moa) will be added.
- This routing strategy will only be applicable to the model groups.
- This new strategy will be configurated in admin.html as well as others.
- The user request will be sent to all agent models and their answers will be collected.
- The collected responses will be prompted to the orchestrator model and is asked to generate the final response considered all the responses that were collected from the agent models.

## Constraints
- In the group that the user will use moa strategy, user must specify an Orchrestrator model.
- The user must also specify multiple (at least two) different Agent models.
- If any of the agent models is unset or the orchestrator is unset, model group must not be allowed to save, and specify the reason why it can not be saved.
- This new strategy will be configurated in admin.html as well as others.

## Acceptance Criteria
- The user should successfully get a response if he/she correctly configured the moa strategy.

## Edge Cases
- The streaming response request that was sent by the user will be handled differently here. Regardless of streaming or not, the requests that will be made to the agent models will be non-streaming, as the user will not reach them, they don't required to be straming. The final response however, will be streaming if the user has requested streaming response.
- If any of the agent models fails, an appropriate fail result will be returned.