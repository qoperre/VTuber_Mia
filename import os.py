import os
from turtle import clone
from dotenv import load_dotenv
from huggingface_hub import InferenceClient

load_dotenv()

client = InferenceClient(
    api_key=os.environ["HF_TOKEN"],
)
def get_completion(prompt):
    completion = client.chat.completions.create(
        model="meta-llama/Llama-3.1-8B-Instruct",
        messages=[
            {
                "role": "user",
                "content": prompt
            }
        ],
    )

    return completion['choices'][0]['message']['content']


while True:
    user_input = input("User: ")
    print("AI: " + get_completion(user_input))       